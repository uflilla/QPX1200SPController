using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.TypeConversion;

namespace QPX1200SPController.Converters
{
    public class BoolToZeroOneConverter : BooleanConverter
    {
        public override string ConvertToString(object value, IWriterRow row, MemberMapData memberMapData)
        {
            if (value is bool b)
            {
                return b ? "1" : "0";
            }

            return base.ConvertToString(value, row, memberMapData);
        }
    }
}
