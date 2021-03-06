﻿using System;
using Pisces.Domain.Models;
using Pisces.Domain.Models.Alleles;
using Pisces.Domain.Types;

namespace Pisces.Calculators
{
    public static class StrandBiasCalculator
    {
        public static void Compute(CalledAllele variant, int[] supportByDirection, int qNoise, double acceptanceCriteria,
            StrandBiasModel strandBiasModel)
        {
            variant.StrandBiasResults = CalculateStrandBiasResults(variant.EstimatedCoverageByDirection, supportByDirection, qNoise, acceptanceCriteria, strandBiasModel);
        }

        /// <summary>
        ///     Assign a strandbias-score to a SNP.
        ///     (using only forward and reverse SNP counts.)
        /// </summary>
        public static BiasResults CalculateStrandBiasResults(int[] coverageByStrandDirection,
            int[] supportByStrandDirection,
            int qNoise, double acceptanceCriteria, StrandBiasModel strandBiasModel)
        {
            var forwardSupport = supportByStrandDirection[(int)DirectionType.Forward];
            var forwardCoverage = coverageByStrandDirection[(int)DirectionType.Forward];
            var reverseSupport = supportByStrandDirection[(int)DirectionType.Reverse];
            var reverseCoverage = coverageByStrandDirection[(int)DirectionType.Reverse];
            var stitchedSupport = supportByStrandDirection[(int)DirectionType.Stitched];
            var stitchedCoverage = coverageByStrandDirection[(int)DirectionType.Stitched];

            var errorRate = Math.Pow(10, -1*qNoise/10f);

            var overallStats = CreateStats(forwardSupport + reverseSupport + stitchedSupport,
                forwardCoverage + reverseCoverage + stitchedCoverage, errorRate, errorRate, strandBiasModel);
            var forwardStats = CreateStats(forwardSupport + stitchedSupport / 2,
                forwardCoverage + stitchedCoverage / 2,
                errorRate, errorRate, strandBiasModel);
            var reverseStats = CreateStats(reverseSupport + stitchedSupport / 2,
                reverseCoverage + stitchedCoverage / 2,
                errorRate, errorRate, strandBiasModel);

            var results = new BiasResults
            {
                ForwardStats = forwardStats,
                ReverseStats = reverseStats,
                OverallStats = overallStats
            };

            results.StitchedStats = CreateStats(stitchedSupport, stitchedCoverage, errorRate, errorRate,
                strandBiasModel);

            var biasResults = AssignBiasScore(overallStats, forwardStats, reverseStats);

            results.BiasScore = biasResults[0];
            results.GATKBiasScore = biasResults[1];
            results.CovPresentOnBothStrands = ((forwardStats.Coverage > 0) && (reverseStats.Coverage > 0));
            results.VarPresentOnBothStrands = ((forwardStats.Support > 0) && (reverseStats.Support > 0));

            //not really fair to call it biased if coverage is in one direction..
            //its ambiguous if variant is found in only one direction.
            if (!results.CovPresentOnBothStrands)
            {
                results.BiasScore = 0;
                results.GATKBiasScore = double.NegativeInfinity;
            }

            var testResults = MathOperations.GetTValue(forwardStats.Frequency, reverseStats.Frequency,
                forwardStats.Coverage,
                reverseStats.Coverage, acceptanceCriteria);

            results.TestScore = testResults[0];
            results.TestAcceptable = ValueAcceptable(acceptanceCriteria, testResults[0], testResults[1]);
            results.BiasAcceptable = (results.BiasScore < acceptanceCriteria);

            return results;
        }

        /// <summary>
        ///     http://www.broadinstitute.org/gsa/wiki/index.php/Understanding_the_Unified_Genotyper%27s_VCF_files
        ///     See section on Strand Bias
        /// </summary>
        // From GATK source:
        //double forwardLod = forwardLog10PofF + reverseLog10PofNull - overallLog10PofF;
        //double reverseLod = reverseLog10PofF + forwardLog10PofNull - overallLog10PofF;
        //
        //// strand score is max bias between forward and reverse strands
        //double strandScore = Math.max(forwardLod, reverseLod);
        //
        //// rescale by a factor of 10
        //strandScore *= 10.0;
        //
        //attributes.put("SB", strandScore);
        private static double[] AssignBiasScore(StrandBiasStats overallStats, StrandBiasStats fwdStats, StrandBiasStats rvsStats)
        {
            var forwardBias = (fwdStats.ChanceVarFreqGreaterThanZero * rvsStats.ChanceFalsePos) /
                                 overallStats.ChanceVarFreqGreaterThanZero;
            var reverseBias = (rvsStats.ChanceVarFreqGreaterThanZero * fwdStats.ChanceFalsePos) /
                                 overallStats.ChanceVarFreqGreaterThanZero;
            var p = Math.Max(forwardBias, reverseBias);

            return new[] { p, MathOperations.PtoGATKBiasScale(p) };
        }

        private static bool ValueAcceptable(double levelOfSignificance, double tvalue, double degreesOfFreedom)
        {
            var alphaOver2 = levelOfSignificance / 2.0;
            var rejectionRegion = 1.282;

            if (degreesOfFreedom < 30)
            {
                return false; //just don't call anything for now.
            }

            //From "Mathematical Statistics With Applications" 6th ed.
            if (alphaOver2 < 0.005)
                rejectionRegion = 2.576;
            else if (alphaOver2 < 0.01)
                rejectionRegion = 2.576;
            else if (alphaOver2 < 0.025)
                rejectionRegion = 2.326;
            else if (alphaOver2 < 0.05)
                rejectionRegion = 1.960;
            else if (alphaOver2 < 0.1)
                rejectionRegion = 1.645;

            if (Math.Abs(tvalue) > rejectionRegion)
            {
                return false;
            }

            return true;
        }

        public static StrandBiasStats CreateStats(double support, double coverage, double noiseFreq, double minDetectableSNP,
            StrandBiasModel strandBiasModel)
        {
            var stats = new StrandBiasStats(support, coverage);
            PopulateStats(stats, noiseFreq, minDetectableSNP, strandBiasModel);

            return stats;
        }

        public static void PopulateStats(StrandBiasStats stats, double noiseFreq, double minDetectableSNP,
            StrandBiasModel strandBiasModel)
        {
            if (stats.Support == 0)
            {
                if (strandBiasModel == StrandBiasModel.Poisson)
                {
                    stats.ChanceFalsePos = 1;
                    stats.ChanceVarFreqGreaterThanZero = 0;
                    stats.ChanceFalseNeg = 0;
                }
                else if (strandBiasModel == StrandBiasModel.Extended)
                {


                    //the chance that we observe the SNP is (minDetectableSNPfreq) for one observation.
                    //the chance that we do not is (1- minDetectableSNPfreq) for one observation.
                    //the chance that we do not observe it, N times in a row is:
                    stats.ChanceVarFreqGreaterThanZero = (Math.Pow(1 - minDetectableSNP, stats.Coverage)); //used in SB metric

                    //liklihood that variant really does not exist
                    //= 1 - chance that it does but you did not see it
                    stats.ChanceFalsePos = 1 - stats.ChanceVarFreqGreaterThanZero; //used in SB metric

                    //Chance a low freq variant is at work in the model, and we did not observe it:
                    stats.ChanceFalseNeg = stats.ChanceVarFreqGreaterThanZero;
                }
            }
            else
            {
                // chance of these observations or less, given min observable variant distribution
                stats.ChanceVarFreqGreaterThanZero = Poisson.Cdf(stats.Support - 1, stats.Coverage * noiseFreq); //used in SB metric
                stats.ChanceFalsePos = 1 - stats.ChanceVarFreqGreaterThanZero; //used in SB metric
                stats.ChanceFalseNeg = Poisson.Cdf(stats.Support, stats.Coverage * minDetectableSNP);
            }

            //Note:
            //
            // Type 1 error is when we rejected the null hypothesis when we should not have. (we have noise, but called a SNP)
            // Type 2 error is when we accepected the alternate when we should not have. (we have a variant, but we did not call it.)
            //
            // Type 1 error is our this.ChanceFalsePos aka p-value.
            // Type 2 error is out this.ChanceFalseNeg
        }
    }
}
