using HidSharp;
using HidSharp.Exceptions;
using HidSharp.Platform.Windows;
using Nefarius.ViGEm.Client;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

using StadiEm.Device;
using StadiEm.Device.Stadia;
using StadiEm.Device.RB3KeyboardController;

namespace StadiEm
{
	class StadiEmContext : ApplicationContext
	{
		NotifyIcon tray;
		Mutex singleInstanceMutex;
		Thread t_monitor;
		List<BaseHIDController> gamepads;
		ViGEmClient client;
		bool runMonitor;
		Stopwatch sw = new Stopwatch();

		public StadiEmContext()
		{
			// Verifies only 1 instance of our application can be running at any point in time.
			// TODO: There's probably a better way to do this. Technically this could be used as a denial of service.
			singleInstanceMutex = new Mutex( true, "StadiEmSingleInstanceMutex", out bool mutexCreated );
			if( !mutexCreated )
			{
				Environment.Exit(0);
			}
			tray = new NotifyIcon
			{
				Icon = Properties.Resources.StadiEm,
				Visible = true,
				ContextMenuStrip = new ContextMenuStrip(),
			};
			tray.ContextMenuStrip.Items.Add( new ToolStripMenuItem( "Quit", null, Quit ) );

			gamepads = new List<BaseHIDController>();
			client = new ViGEmClient();

			t_monitor = new Thread( () => Monitor() )
			{
				Name = "Monitor",
				Priority = ThreadPriority.BelowNormal,
			};

			// spin up the threads!
			runMonitor = true;
			t_monitor.Start();
		}

		public void reEnableDevice( string deviceInstanceId )
		{
			bool success;
			Guid hidGuid = NativeMethods.HidD_GetHidGuid();
			NativeMethods.HDEVINFO deviceInfoSet = NativeMethods.SetupDiGetClassDevs( hidGuid, deviceInstanceId, IntPtr.Zero, NativeMethods.DIGCF.Present | NativeMethods.DIGCF.DeviceInterface );
			NativeMethods.SP_DEVINFO_DATA deviceInfoData = new NativeMethods.SP_DEVINFO_DATA();
			deviceInfoData.Size = Marshal.SizeOf( deviceInfoData );
			success = NativeMethods.SetupDiEnumDeviceInfo( deviceInfoSet, 0, ref deviceInfoData );
			if( !success )
			{
				throw new Exception( "Error getting device info data, error code = " + Marshal.GetLastWin32Error() );
			}
			success = NativeMethods.SetupDiEnumDeviceInfo( deviceInfoSet, 1, ref deviceInfoData ); // Checks that we have a unique device
			if( success )
			{
				throw new Exception( "Can't find unique device" );
			}

			NativeMethods.SP_PROPCHANGE_PARAMS propChangeParams = new NativeMethods.SP_PROPCHANGE_PARAMS();
			propChangeParams.classInstallHeader.cbSize = Marshal.SizeOf( propChangeParams.classInstallHeader );
			propChangeParams.classInstallHeader.installFunction = NativeMethods.DIF_PROPERTYCHANGE;
			propChangeParams.stateChange = NativeMethods.DICS_DISABLE;
			propChangeParams.scope = NativeMethods.DICS_FLAG_GLOBAL;
			propChangeParams.hwProfile = 0;
			success = NativeMethods.SetupDiSetClassInstallParams( deviceInfoSet, ref deviceInfoData, ref propChangeParams, Marshal.SizeOf( propChangeParams ) );
			if( !success )
			{
				throw new Exception( "Error setting class install params, error code = " + Marshal.GetLastWin32Error() );
			}
			success = NativeMethods.SetupDiCallClassInstaller( NativeMethods.DIF_PROPERTYCHANGE, deviceInfoSet, ref deviceInfoData );
			// TEST: If previous SetupDiCallClassInstaller fails, just continue
			// otherwise device will likely get permanently disabled.
			/*if (!success)
            {
                throw new Exception("Error disabling device, error code = " + Marshal.GetLastWin32Error());
            }
            */

			//System.Threading.Thread.Sleep(50);
			sw.Restart();
			while( sw.ElapsedMilliseconds < 50 )
			{
				// Use SpinWait to keep control of current thread. Using Sleep could potentially
				// cause other events to get run out of order
				System.Threading.Thread.SpinWait( 100 );
			}
			sw.Stop();

			propChangeParams.stateChange = NativeMethods.DICS_ENABLE;
			success = NativeMethods.SetupDiSetClassInstallParams( deviceInfoSet, ref deviceInfoData, ref propChangeParams, Marshal.SizeOf( propChangeParams ) );
			if( !success )
			{
				throw new Exception( "Error setting class install params, error code = " + Marshal.GetLastWin32Error() );
			}
			success = NativeMethods.SetupDiCallClassInstaller( NativeMethods.DIF_PROPERTYCHANGE, deviceInfoSet, ref deviceInfoData );
			if( !success )
			{
				throw new Exception( "Error enabling device, error code = " + Marshal.GetLastWin32Error() );
			}

			//System.Threading.Thread.Sleep(50);
			sw.Restart();
			while( sw.ElapsedMilliseconds < 50 )
			{
				// Use SpinWait to keep control of current thread. Using Sleep could potentially
				// cause other events to get run out of order
				System.Threading.Thread.SpinWait( 100 );
			}
			sw.Stop();

			NativeMethods.SetupDiDestroyDeviceInfoList( deviceInfoSet );
		}
		private static string devicePathToInstanceId( string devicePath )
		{
			string deviceInstanceId = devicePath;
			deviceInstanceId = deviceInstanceId.Remove( 0, deviceInstanceId.LastIndexOf( '\\' ) + 1 );
			deviceInstanceId = deviceInstanceId.Remove( deviceInstanceId.LastIndexOf( '{' ) );
			deviceInstanceId = deviceInstanceId.Replace( '#', '\\' );
			if( deviceInstanceId.EndsWith( "\\" ) )
			{
				deviceInstanceId = deviceInstanceId.Remove( deviceInstanceId.Length - 1 );
			}

			return deviceInstanceId;
		}

		void Monitor()
		{
			while( runMonitor )
			{
				var compatibleDevices = DeviceList.Local.GetHidDevices( StadiaController.VID, StadiaController.PID );
				compatibleDevices = compatibleDevices.Concat( DeviceList.Local.GetHidDevices( RB3KeyboardController.VID, RB3KeyboardController.PID ) );
				var existingDevices = gamepads.Select( g => g._device ).ToList();
				var newDevices = compatibleDevices.Where( d => !existingDevices.Select( e => e.DevicePath ).Contains( d.DevicePath ) );
				foreach( var gamepad in gamepads.ToList() )
				{
					if( !gamepad.running )
					{
						gamepads.Remove( gamepad );
					}
					if( gamepads.Count == 0 )
					{
						GCSettings.LatencyMode = GCLatencyMode.Interactive;
					}
				}
				foreach( var deviceInstance in newDevices )
				{
					var device = deviceInstance;
					HidStream stream = null;
					try
					{
						reEnableDevice( devicePathToInstanceId( device.DevicePath ) );
						OpenConfiguration oc = new OpenConfiguration();
						oc.SetOption( OpenOption.Exclusive, true );
						oc.SetOption( OpenOption.Priority, OpenPriority.VeryHigh );
						stream = device.Open( oc );
					}
					catch( DeviceIOException )
					{
						try
						{
							stream = device.Open();
							NotifyUser( "Unable to open device in exclusive mode. Try reconnecting the controller; you may also need to close other apps (Steam, Discord, internet browser, etc)." );
						}
						catch( DeviceIOException )
						{
							// well, shit happens I guess. give up on this round of Monitor. I've run into this when unplugging RB3 keyboard dongles.
							goto IOErrorSkip;
						}
					}

					var usedIndexes = gamepads.Select( g => g._index );
					int index = 1;
					while( usedIndexes.Contains( index ) )
					{
						index++;
					}

					switch( device.VendorID )
					{
						case StadiaController.VID:
							if( device.ProductID == StadiaController.PID )
							{
								gamepads.Add( new StadiaController( device, stream, client, index ) );
							}
							break;
						case RB3KeyboardController.VID:
							if( device.ProductID == RB3KeyboardController.PID )
							{
								gamepads.Add( new RB3KeyboardController( device, stream, client, index ) );
							}
							break;
					}
					GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

				}
IOErrorSkip:
				Thread.Sleep( 1000 );
			}
		}

		void Quit( object sender, EventArgs e )
		{
			if( tray != null )
			{
				tray.Visible = false;
			}
			runMonitor = false;
			t_monitor.Join();
			foreach( var gamepad in gamepads )
			{
				gamepad.unplug();
			}
			Application.Exit();
		}

		void NotifyUser( string message )
		{
			this.tray.BalloonTipTitle = "StadiEm";
			this.tray.BalloonTipText = message;
			this.tray.ShowBalloonTip( 1000 );
		}
	}
}
