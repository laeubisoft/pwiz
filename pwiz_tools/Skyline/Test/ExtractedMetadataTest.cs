﻿/*
 * Original author: Nicholas Shulman <nicksh .at. u.washington.edu>,
 *                  MacCoss Lab, Department of Genome Sciences, UW
 *
 * Copyright 2020 University of Washington - Seattle, WA
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
using System.IO;
using System.Xml;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using pwiz.Common.DataBinding;
using pwiz.Skyline.Model.DocSettings.MetadataExtraction;
using pwiz.Skyline.Util;
using pwiz.SkylineTestUtil;

namespace pwiz.SkylineTest
{
    [TestClass]
    public class ExtractedMetadataTest : AbstractUnitTest
    {
        [TestMethod]
        public void TestMetadataRuleSerialization()
        {
            var ruleSet = new MetadataRuleSet("test", new []
            {
                new MetadataRule().ChangeSource(PropertyPath.Parse("foo"))
                    .ChangePattern("[a-z]")
                    .ChangeReplacement("$0")
                    .ChangeTarget(PropertyPath.Parse("bar"))
                
            });
            Deserializable(ruleSet);
        }

        public static void Deserializable<T>(T obj) where T: class
        {
            string expected = null;
            AssertEx.RoundTrip(obj, ref expected);
            var xmlElementHelper = new XmlElementHelper<T>();
            using (var xmlReader = XmlReader.Create(new StringReader(expected)))
            {
                while (!xmlReader.IsStartElement())
                {
                    xmlReader.Read();
                }
                var obj2 = xmlElementHelper.Deserialize(xmlReader);
                Assert.AreEqual(obj, obj2);
            }
        }
    }
}