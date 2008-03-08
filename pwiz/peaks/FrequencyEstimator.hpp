//
// FrequencyEstimator.hpp
//
//
// Original author: Darren Kessner <Darren.Kessner@cshs.org>
//
// Copyright 2006 Louis Warschaw Prostate Cancer Center
//   Cedars Sinai Medical Center, Los Angeles, California  90048
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


#ifndef _FREQUENCYESTIMATOR_HPP_ 
#define _FREQUENCYESTIMATOR_HPP_ 


#include "data/FrequencyData.hpp"
#include "data/PeakData.hpp"


namespace pwiz {
namespace peaks {


/// Interface for frequency estimator modules. 
class FrequencyEstimator
{
    public:

    typedef pwiz::data::FrequencyData FrequencyData;
    typedef pwiz::data::peakdata::Peak Peak;

    virtual Peak estimate(const FrequencyData& fd, 
                          const Peak& initialEstimate) const = 0; 

    virtual ~FrequencyEstimator(){}
};


} // namespace peaks
} // namespace pwiz


#endif // _FREQUENCYESTIMATOR_HPP_ 


