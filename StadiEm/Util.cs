using System;
using System.Collections.Generic;
using System.Text;

namespace StadiEm
{
	public static class Util
	{
		public static int ConvertRangeInt( int input, int in_min, int in_max, int out_min, int out_max )
		{
			return ( ( input - in_min ) * ( out_max - out_min ) / ( in_max - in_min ) ) + out_min;
		}

		public static int ConvertRangeFloat( int input, int in_min, int in_max, int out_min, int out_max )
		{
			return (int)( (float)(( input - in_min ) * ( out_max - out_min )) / ( in_max - in_min ) ) + out_min;
		}
	}
}
