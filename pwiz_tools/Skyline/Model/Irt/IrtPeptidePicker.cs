﻿/*
 * Original author: Kaipo Tamura <kaipot .at. u.washington.edu>,
 *                  MacCoss Lab, Department of Genome Sciences, UW
 *
 * Copyright 2019 University of Washington - Seattle, WA
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using pwiz.Common.Collections;
using pwiz.Common.SystemUtil;
using pwiz.Skyline.Model.DocSettings;
using pwiz.Skyline.Model.Results.Scoring;
using pwiz.Skyline.SettingsUI;
using pwiz.Skyline.Util;

namespace pwiz.Skyline.Model.Irt
{
    public class IrtPeptidePicker
    {
        private ScoredPeptide[] _scoredPeptides;
        private ScoredPeptide[] _cirtPeptides;
        private readonly Dictionary<Target, double> _cirtAll;

        public IrtPeptidePicker()
        {
            _scoredPeptides = null;
            _cirtPeptides = null;
            _cirtAll = IrtStandard.CIRT.Peptides.ToDictionary(pep => pep.ModifiedTarget, pep => pep.Irt);
        }

        public bool HasScoredPeptides => _scoredPeptides != null && _scoredPeptides.Length > 0;
        public int CirtPeptideCount => _cirtPeptides != null ? _cirtPeptides.Length : 0;
        private double MinRt => _scoredPeptides.First().Peptide.RetentionTime;
        private double MaxRt => _scoredPeptides.Last().Peptide.RetentionTime;
        private double RtRange => MaxRt - MinRt;
        private IEnumerable<double> BucketBoundaries => new[]
        {
            MinRt + RtRange * 1 / 8,
            MinRt + RtRange * 2 / 8,
            MinRt + RtRange * 4 / 8,
            MinRt + RtRange * 6 / 8,
            MinRt + RtRange * 7 / 8,
            double.MaxValue
        };

        public double? CirtIrt(Target target)
        {
            return _cirtAll.TryGetValue(target, out var irt) ? irt : (double?) null;
        }

        public void ScorePeptides(SrmDocument doc, IProgressMonitor progressMonitor)
        {
            var model = doc.Settings.PeptideSettings.Integration.PeakScoringModel;
            if (model == null || !model.IsTrained)
                model = LegacyScoringModel.DEFAULT_MODEL;

            var mProphetResultsHandler = new MProphetResultsHandler(doc, model);
            mProphetResultsHandler.ScoreFeatures(progressMonitor, true);
            if (progressMonitor.IsCanceled)
                return;

            var scoredPeptidesDict = new Dictionary<Target, ScoredPeptide>();
            foreach (var nodePep in doc.Molecules.Where(pep => pep.PercentileMeasuredRetentionTime.HasValue && !pep.IsDecoy))
            {
                var allStats = doc.MeasuredResults.MSDataFileInfos
                    .Select(info => mProphetResultsHandler.GetPeakFeatureStatistics(nodePep.Id.GlobalIndex, info.FileId.GlobalIndex))
                    .Where(stats => stats != null).ToArray();
                var value = float.MaxValue;
                if (allStats.Length > 0)
                {
                    value = model is MProphetPeakScoringModel
                        ? allStats.Select(stats => stats.QValue.Value).Max()
                        : -allStats.Select(stats => stats.BestScore).Min();
                }
                if (!scoredPeptidesDict.TryGetValue(nodePep.ModifiedTarget, out var existing) || value < existing.Score)
                    scoredPeptidesDict[nodePep.ModifiedTarget] = new ScoredPeptide(
                        new MeasuredPeptide(doc.Settings.GetModifiedSequence(nodePep), nodePep.PercentileMeasuredRetentionTime.Value), nodePep, value);
            }
            _scoredPeptides = scoredPeptidesDict.Values.OrderBy(pep => pep.Peptide.RetentionTime).ToArray();
            _cirtPeptides = _scoredPeptides.Where(pep => _cirtAll.ContainsKey(pep.Peptide.Target)).ToArray();
        }

        public bool TryGetCirtRegression(int count, out RegressionLine regression, out IEnumerable<Tuple<DbIrtPeptide, PeptideDocNode>> matchedPeptides)
        {
            matchedPeptides = null;
            var success = TryGetCirtRegression(count, out regression, out List<ScoredPeptide> peptides);
            if (success)
            {
                matchedPeptides = peptides.Select(pep => Tuple.Create(
                    new DbIrtPeptide(pep.Peptide.Target, _cirtAll[pep.Peptide.Target], true, TimeSource.peak),
                    pep.NodePep));
            }
            return success;
        }

        private bool TryGetCirtRegression(int count, out RegressionLine regression, out List<ScoredPeptide> peptides)
        {
            peptides = new List<ScoredPeptide>(_cirtPeptides);
            var rts = _cirtPeptides.Select(pep => pep.Peptide.RetentionTime).ToList();
            var irts = _cirtPeptides.Select(pep => _cirtAll[pep.Peptide.Target]).ToList();
            if (!RCalcIrt.TryGetRegressionLine(rts, irts, count, out regression, out var removed))
                return false;

            for (var i = peptides.Count - 1; i >= 0; i--)
            {
                if (removed.Contains(Tuple.Create(rts[i], irts[i])))
                    peptides.RemoveAt(i);
            }
            return peptides.Count >= count;
        }

        /// <summary>
        /// This algorithm will determine a number of evenly spaced retention times for the given document,
        /// and then determine an optimal set of peptides from the document. That is, a set of peptides that
        /// are as close as possible to the chosen retention times.
        ///
        /// The returned list is guaranteed to be sorted by retention time.
        /// </summary>
        /// <param name="count">The number of peptides to be picked</param>
        /// <param name="exclude">Peptides that cannot be picked</param>
        /// <param name="cirt">Use CiRT peptides, if possible</param>
        public List<MeasuredPeptide> Pick(int count, ICollection<Target> exclude, bool cirt)
        {
            PeptideBucket<ScoredPeptide>[] buckets = null;
            if (cirt && TryGetCirtRegression(count, out _, out List<ScoredPeptide> scoredCirtPeptides))
            {
                // If each bucket contains at least one, prompt to use CiRT peptides
                var cirtBuckets = PeptideBucket<ScoredPeptide>.BucketPeptides(scoredCirtPeptides, BucketBoundaries);
                if (cirtBuckets.All(bucket => !bucket.Empty))
                    buckets = cirtBuckets;
            }

            if (buckets == null)
                buckets = exclude == null || exclude.Count == 0
                    ? PeptideBucket<ScoredPeptide>.BucketPeptides(_scoredPeptides, BucketBoundaries)
                    : PeptideBucket<ScoredPeptide>.BucketPeptides(_scoredPeptides.Where(pep => !exclude.Contains(pep.Peptide.Target)), BucketBoundaries);
            var endBuckets = new[] { buckets.First(), buckets.Last() };
            var midBuckets = buckets.Skip(1).Take(buckets.Length - 2).ToArray();

            var bestPeptides = new List<MeasuredPeptide>();
            while (bestPeptides.Count < count && buckets.Any(bucket => !bucket.Empty))
            {
                bestPeptides.AddRange(PeptideBucket<ScoredPeptide>.Pop(endBuckets, endBuckets.Length, true)
                    .Take(Math.Min(endBuckets.Length, count - bestPeptides.Count)).Select(pep => pep.Peptide));
                bestPeptides.AddRange(PeptideBucket<ScoredPeptide>.Pop(midBuckets, midBuckets.Length, false)
                    .Take(Math.Min(midBuckets.Length, count - bestPeptides.Count)).Select(pep => pep.Peptide));
            }
            bestPeptides.Sort((x, y) => x.RetentionTime.CompareTo(y.RetentionTime));
            return bestPeptides.Select(pep => new MeasuredPeptide(pep)).ToList();
        }

        public static IEnumerable<Target> Pick(int count, DbIrtPeptide[] peptides, IEnumerable<Target> outliers)
        {
            var targets = new TargetMap<List<DbIrtPeptide>>(peptides.Select(pep =>
                new KeyValuePair<Target, List<DbIrtPeptide>>(pep.ModifiedTarget, new List<DbIrtPeptide>())));
            foreach (var pep in peptides)
                targets[pep.ModifiedTarget].Add(pep);
            var distinctPeps = new List<DbIrtPeptide>();
            foreach (var list in targets.Values)
            {
                if (list.Count == 0)
                    continue;

                var median = new Statistics(list.Select(pep => pep.Irt)).Median();
                DbIrtPeptide best = null;
                var minDiff = double.MaxValue;
                foreach (var pep in list)
                {
                    var diff = Math.Abs(pep.Irt - median);
                    if (diff < minDiff)
                    {
                        minDiff = diff;
                        best = pep;
                    }
                }
                distinctPeps.Add(best);
            }

            var outlierMap = new TargetMap<bool>(outliers.Select(target => new KeyValuePair<Target, bool>(target, true)));
            if (distinctPeps.Count(pep => !outlierMap.ContainsKey(pep.ModifiedTarget)) >= count)
            {
                // don't use outliers if we have enough other values
                distinctPeps.RemoveAll(pep => outlierMap.ContainsKey(pep.ModifiedTarget));
            }

            distinctPeps.Sort((x, y) => x.Irt.CompareTo(y.Irt));
            var minIrt = distinctPeps.First().Irt;
            var maxIrt = distinctPeps.Last().Irt;
            var gradientLength = maxIrt - minIrt;
            for (var i = 0; i < count; i++)
            {
                var targetRt = minIrt + i * (gradientLength / (count - 1));
                for (var j = 0; j < distinctPeps.Count; j++)
                {
                    if (j + 1 > distinctPeps.Count - 1 ||
                        Math.Abs(distinctPeps[j].Irt - targetRt) < Math.Abs(distinctPeps[j + 1].Irt - targetRt))
                    {
                        yield return distinctPeps[j].ModifiedTarget;
                        distinctPeps.RemoveAt(j);
                        break;
                    }
                }
            }
        }

        public static void SetStandards(IEnumerable<DbIrtPeptide> peptides, IEnumerable<Target> standards)
        {
            var standardMap = new TargetMap<bool>(standards.Select(target => new KeyValuePair<Target, bool>(target, true)));
            foreach (var pep in peptides.Where(pep => standardMap.ContainsKey(pep.ModifiedTarget)))
                pep.Standard = true;
        }

        public static void SetStandards(IEnumerable<DbIrtPeptide> peptides, IrtStandard standard)
        {
            SetStandards(peptides, standard.Peptides.Select(pep => pep.ModifiedTarget));
        }

        private interface IBucketable
        {
            double Time { get; }
            float Score { get; } // lower scores get picked first
        }

        private class ScoredPeptide : IBucketable
        {
            public MeasuredPeptide Peptide { get; }
            public PeptideDocNode NodePep { get; }
            public float Score { get; }

            public double Time => Peptide.RetentionTime;

            public ScoredPeptide(MeasuredPeptide peptide, PeptideDocNode nodePep, float score)
            {
                Peptide = peptide;
                NodePep = nodePep;
                Score = score;
            }
        }

        private class PeptideBucket<T> where T : IBucketable
        {
            private readonly double _maxTime;
            private readonly List<T> _peptides;

            public bool Empty => _peptides.Count == 0;

            private PeptideBucket(double maxTime)
            {
                _maxTime = maxTime;
                _peptides = new List<T>();
            }

            private float? Peek()
            {
                return !Empty ? (float?)_peptides.First().Score : null;
            }

            private T Pop()
            {
                if (Empty)
                    return default(T);
                var pep = _peptides.First();
                _peptides.RemoveAt(0);
                return pep;
            }

            public static PeptideBucket<T>[] BucketPeptides(IEnumerable<T> peptides, IEnumerable<double> rtBoundaries)
            {
                // peptides must be sorted by retention time (low to high)
                var buckets = rtBoundaries.OrderBy(x => x).Select(boundary => new PeptideBucket<T>(boundary)).ToArray();
                var curBucketIdx = 0;
                var curBucket = buckets[0];
                foreach (var pep in peptides)
                {
                    if (pep.Time > curBucket._maxTime)
                        curBucket = buckets[++curBucketIdx];
                    curBucket._peptides.Add(pep);
                }
                buckets.ForEach(bucket => bucket._peptides.Sort((x, y) => x.Score.CompareTo(y.Score)));
                return buckets;
            }

            public static IEnumerable<T> Pop(PeptideBucket<T>[] buckets, int num, bool limitOne)
            {
                // buckets must be sorted by score (best to worst)
                var popped = 0;
                while (popped < num)
                {
                    var validBuckets = buckets.Where(bucket => !bucket.Empty).OrderBy(bucket => bucket.Peek().Value).ToArray();
                    foreach (var bucket in validBuckets)
                    {
                        yield return bucket.Pop();
                        if (++popped == num)
                            yield break;
                    }
                    if (validBuckets.Length == 0 || limitOne)
                        yield break;
                }
            }
        }
    }
}
