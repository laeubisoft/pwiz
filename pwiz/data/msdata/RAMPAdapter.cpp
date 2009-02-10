//
// RAMPAdapter.cpp
//
//
// Original author: Darren Kessner <Darren.Kessner@cshs.org>
//
// Copyright 2008 Spielberg Family Center for Applied Proteomics
//   Cedars-Sinai Medical Center, Los Angeles, California  90048
//
// Licensed under the Apache License, Version 2.0 (the "License"); 
// you may not use this file except in compliance with the License. 
// You may obtain a copy of the License at 
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software 
// distributed under the License is distributed on an "AS IS" BASIS, 
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. 
// See the License for the specific language governing permissions and 
// limitations under the License.
//


#define PWIZ_SOURCE

#include "RAMPAdapter.hpp"
#include "MSDataFile.hpp"
#include "LegacyAdapter.hpp"
#include "CVTranslator.hpp"
#include "boost/lexical_cast.hpp"
#include "boost/static_assert.hpp"
#include <stdexcept>
#include <iostream>
#include <algorithm>


namespace pwiz {
namespace msdata {


using namespace std;
using boost::lexical_cast;
using boost::bad_lexical_cast;


class RAMPAdapter::Impl
{
    public:

    Impl(const string& filename) 
    :   msd_(filename) 
    {
        if (!msd_.run.spectrumListPtr.get())
            throw runtime_error("[RAMPAdapter] Null spectrumListPtr.");
    }

    size_t scanCount() const
    {
        return msd_.run.spectrumListPtr->size();
    }

    size_t index(int scanNumber) const 
    {
        IndexList result = msd_.run.spectrumListPtr->findNameValue("scan", lexical_cast<string>(scanNumber));
        return result.empty() ? 0 : result[0];
    }

    void getScanHeader(size_t index, ScanHeaderStruct& result) const;
    void getScanPeaks(size_t index, std::vector<double>& result) const;
    void getRunHeader(RunHeaderStruct& result) const;
    void getInstrument(InstrumentStruct& result) const;

    private:
    MSDataFile msd_;
    CVTranslator cvTranslator_;
};


namespace {

double retentionTime(const Scan& scan)
{
    CVParam param = scan.cvParam(MS_scan_time);
    if (param.units == UO_second) 
        return param.valueAs<double>();
    else if (param.units == UO_minute) 
        return param.valueAs<double>() * 60;
    return 0;
}

} // namespace


void RAMPAdapter::Impl::getScanHeader(size_t index, ScanHeaderStruct& result) const
{
    const SpectrumList& spectrumList = *msd_.run.spectrumListPtr;
    SpectrumPtr spectrum = spectrumList.spectrum(index);

    Scan dummy;
    Scan& scan = spectrum->scanList.scans.empty() ? dummy : spectrum->scanList.scans[0];

    result.seqNum = static_cast<int>(index + 1);
    result.acquisitionNum = id::valueAs<int>(spectrum->id, "scan");
    result.msLevel = spectrum->cvParam(MS_ms_level).valueAs<int>();
    result.peaksCount = static_cast<int>(spectrum->defaultArrayLength);
    result.totIonCurrent = spectrum->cvParam(MS_total_ion_current).valueAs<double>();
    result.retentionTime = scan.cvParam(MS_scan_time).timeInSeconds();
    result.basePeakMZ = spectrum->cvParam(MS_base_peak_m_z).valueAs<double>();    
    result.basePeakIntensity = spectrum->cvParam(MS_base_peak_intensity).valueAs<double>();    
    result.collisionEnergy = 0;
    result.ionisationEnergy = spectrum->cvParam(MS_ionization_energy).valueAs<double>();
    result.lowMZ = spectrum->cvParam(MS_lowest_observed_m_z).valueAs<double>();        
    result.highMZ = spectrum->cvParam(MS_highest_observed_m_z).valueAs<double>();        
    result.precursorScanNum = 0;
    result.precursorMZ = 0;
    result.precursorCharge = 0;
    result.precursorIntensity = 0;

    if (!spectrum->precursors.empty())
    {
        const Precursor& precursor = spectrum->precursors[0];
        result.collisionEnergy = precursor.activation.cvParam(MS_collision_energy).valueAs<double>();
        size_t precursorIndex = msd_.run.spectrumListPtr->find(precursor.spectrumID);

        if (precursorIndex < spectrumList.size())
            result.precursorScanNum = id::valueAs<int>(spectrumList.spectrum(precursorIndex)->id, "scan");

        if (!precursor.selectedIons.empty())
        {
            result.precursorMZ = precursor.selectedIons[0].cvParam(MS_selected_ion_m_z).valueAs<double>();
            result.precursorCharge = precursor.selectedIons[0].cvParam(MS_charge_state).valueAs<int>();
            result.precursorIntensity = precursor.selectedIons[0].cvParam(MS_intensity).valueAs<double>();
        }
    }

    BOOST_STATIC_ASSERT(SCANTYPE_LENGTH > 4);
    memset(result.scanType, 0, SCANTYPE_LENGTH);
    CVParam paramScanType = scan.cvParamChild(MS_scanning_method);
    if (paramScanType.cvid == MS_full_scan) strcpy(result.scanType, "Full");
    if (paramScanType.cvid == MS_zoom_scan) strcpy(result.scanType, "Zoom");

    result.mergedScan = 0; // TODO 
    result.mergedResultScanNum = 0; // TODO 
    result.mergedResultStartScanNum = 0; // TODO 
    result.mergedResultEndScanNum = 0; // TODO 
    result.filePosition = spectrum->sourceFilePosition; 
}


void RAMPAdapter::Impl::getScanPeaks(size_t index, std::vector<double>& result) const
{
    SpectrumPtr spectrum = msd_.run.spectrumListPtr->spectrum(index, true);

    result.clear();
    result.resize(spectrum->defaultArrayLength * 2);
    if (spectrum->defaultArrayLength == 0) return;

    spectrum->getMZIntensityPairs(reinterpret_cast<MZIntensityPair*>(&result[0]), 
                                  spectrum->defaultArrayLength);
}


void RAMPAdapter::Impl::getRunHeader(RunHeaderStruct& result) const
{
    const SpectrumList& spectrumList = *msd_.run.spectrumListPtr;
    result.scanCount = static_cast<int>(spectrumList.size());

    result.lowMZ = 0; // TODO
    result.highMZ = 0; // TODO
    result.startMZ = 0; // TODO
    result.endMZ = 0; // TODO

    if (spectrumList.size() == 0) return;

    Scan dummy;

    SpectrumPtr firstSpectrum = spectrumList.spectrum(0, false);
    Scan& firstScan = firstSpectrum->scanList.scans.empty() ? dummy : firstSpectrum->scanList.scans[0];
    result.dStartTime = retentionTime(firstScan);

    SpectrumPtr lastSpectrum = spectrumList.spectrum(spectrumList.size()-1, false);
    Scan& lastScan = lastSpectrum->scanList.scans.empty() ? dummy : lastSpectrum->scanList.scans[0];
    result.dEndTime = retentionTime(lastScan);
}


namespace {
inline void copyInstrumentString(char* to, const string& from)
{
    strncpy(to, from.substr(0,INSTRUMENT_LENGTH-1).c_str(), INSTRUMENT_LENGTH);
}
} // namespace


void RAMPAdapter::Impl::getInstrument(InstrumentStruct& result) const
{
    const InstrumentConfiguration& instrumentConfiguration = 
        (!msd_.instrumentConfigurationPtrs.empty() && msd_.instrumentConfigurationPtrs[0].get()) ?
        *msd_.instrumentConfigurationPtrs[0] :
        InstrumentConfiguration(); // temporary bound to const reference 

    // this const_cast is ok since we're only calling const functions,
    // but we wish C++ had "const constructors"
    const LegacyAdapter_Instrument adapter(const_cast<InstrumentConfiguration&>(instrumentConfiguration), cvTranslator_); 

    copyInstrumentString(result.manufacturer, adapter.manufacturer());
    copyInstrumentString(result.model, adapter.model());
    copyInstrumentString(result.ionisation, adapter.ionisation());
    copyInstrumentString(result.analyzer, adapter.analyzer());
    copyInstrumentString(result.detector, adapter.detector());
}


//
// RAMPAdapter
//


PWIZ_API_DECL RAMPAdapter::RAMPAdapter(const std::string& filename) : impl_(new Impl(filename)) {}
PWIZ_API_DECL size_t RAMPAdapter::scanCount() const {return impl_->scanCount();}
PWIZ_API_DECL size_t RAMPAdapter::index(int scanNumber) const {return impl_->index(scanNumber);}
PWIZ_API_DECL void RAMPAdapter::getScanHeader(size_t index, ScanHeaderStruct& result) const {impl_->getScanHeader(index, result);}
PWIZ_API_DECL void RAMPAdapter::getScanPeaks(size_t index, std::vector<double>& result) const {impl_->getScanPeaks(index, result);}
PWIZ_API_DECL void RAMPAdapter::getRunHeader(RunHeaderStruct& result) const {impl_->getRunHeader(result);}
PWIZ_API_DECL void RAMPAdapter::getInstrument(InstrumentStruct& result) const {impl_->getInstrument(result);}


} // namespace msdata
} // namespace pwiz


