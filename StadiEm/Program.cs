using System;
using System.Windows.Forms;

namespace StadiEm
{
	static class Program
	{
		/// <summary>
		/// The main entry point for the application.
		/// </summary>
		[STAThread]
		static void Main()
		{
			// NOTE: StadiEmContext verifies that only one instance of the application is created.
			//       StadiEmContext is effectively our Main(), don't do anything fancy here.
			Application.EnableVisualStyles();
			Application.SetCompatibleTextRenderingDefault( false );
			Application.Run( new StadiEmContext() );
		}
	}
}
