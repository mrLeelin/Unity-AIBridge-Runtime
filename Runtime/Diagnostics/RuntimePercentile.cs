using System;
using System.Collections.Generic;

namespace AIBridge.Runtime.Diagnostics
{
    public static class RuntimePercentile
    {
        public static double Calculate(IList<double> sortedValues, double percentile)
        {
            if (sortedValues == null || sortedValues.Count == 0)
            {
                return 0d;
            }

            if (sortedValues.Count == 1)
            {
                return sortedValues[0];
            }

            if (percentile <= 0d)
            {
                return sortedValues[0];
            }

            if (percentile >= 100d)
            {
                return sortedValues[sortedValues.Count - 1];
            }

            var position = (percentile / 100d) * (sortedValues.Count - 1);
            var lowerIndex = (int)Math.Floor(position);
            var upperIndex = (int)Math.Ceiling(position);
            if (lowerIndex == upperIndex)
            {
                return sortedValues[lowerIndex];
            }

            var weight = position - lowerIndex;
            return sortedValues[lowerIndex] + (sortedValues[upperIndex] - sortedValues[lowerIndex]) * weight;
        }
    }
}
