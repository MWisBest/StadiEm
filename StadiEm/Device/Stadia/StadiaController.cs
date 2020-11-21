using HidSharp;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Threading;

namespace StadiEm.Device.Stadia
{
	public class StadiaController : BaseHIDController
	{
		public const ushort VID = 0x18D1;
		public const ushort PID = 0x9400;

		public const int DATA_ID = 0x00;
		public const int DATA_DPAD = 0x01;
		public const int DATA_BUTTONS_1 = 0x02;
		public const int DATA_BUTTONS_2 = 0x03;
		public const int DATA_LX = 0x04;
		public const int DATA_LY = 0x05;
		public const int DATA_RX = 0x06;
		public const int DATA_RY = 0x07;
		public const int DATA_L2 = 0x08;
		public const int DATA_R2 = 0x09;

		public Dictionary<Xbox360Property, bool> dictttt;


		public Thread ssThread, vidThread, inputThread, writeThread;
		private AutoResetEvent writeEvent;
		private ConcurrentQueue<byte[]> writeQueue;

		public StadiaController( HidDevice device, HidStream stream, ViGEmClient client, int index ) : base( device, stream, client, index )
		{
			target360.FeedbackReceived += this.Target360_FeedbackReceived;
			targetDS4.FeedbackReceived += this.TargetDS4_FeedbackReceived;

			if( !pluggedIn360 )
			{
				pluggedIn360 = true;
				target360.Connect();
			}

			writeEvent = new AutoResetEvent( false );
			writeQueue = new ConcurrentQueue<byte[]>();

			inputThread = new Thread( () => input_thread() );
			inputThread.Name = "Controller #" + index + " Input";

			writeThread = new Thread( () => write_thread() );
			writeThread.Name = "Controller #" + index + " Output";

			writeThread.Start();
			inputThread.Start();
		}

		private void Target360_FeedbackReceived( object sender, Xbox360FeedbackReceivedEventArgs e )
		{
			vibrate( e.LargeMotor, e.SmallMotor );
		}

		private void TargetDS4_FeedbackReceived( object sender, DualShock4FeedbackReceivedEventArgs e )
		{
			vibrate( e.LargeMotor, e.SmallMotor );
		}

		private void vibrate( byte largeMotor, byte smallMotor )
		{
			byte[] vibReport = { 0x05, largeMotor, largeMotor, smallMotor, smallMotor };

			writeQueue.Enqueue( vibReport );
			try
			{
				writeEvent.Set();
			}
			catch( ObjectDisposedException )
			{
			}
		}

		public void write_thread()
		{
			byte[] queuedWrite;
			bool peekSuccess, dequeueSuccess, writeSuccess;
			int peekFailCounter = 0, dequeueFailCounter = 0;
			_stream.WriteTimeout = 1000;
			while( running )
			{
				writeEvent.WaitOne( 200 );
				while( !writeQueue.IsEmpty )
				{
					peekSuccess = writeQueue.TryPeek( out queuedWrite );
					if( peekSuccess )
					{
						peekFailCounter = 0;
						try
						{
							// null checking _stream is useless because it can get closed while we're blocking on this write.
							_stream.Write( queuedWrite );
							writeSuccess = true;
						}
						catch( TimeoutException )
						{
							writeSuccess = false;
						}
						catch( IOException e )
						{
							if( e.InnerException != null &&
								e.InnerException is Win32Exception exception &&
								( exception.NativeErrorCode.Equals( 0x0000048F ) || exception.NativeErrorCode.Equals( 0x000001B1 ) ) )
							{
								goto WRITE_STREAM_FAILURE;
							}
							else
							{
								throw e;
							}
						}
						catch( ObjectDisposedException )
						{
							goto WRITE_STREAM_FAILURE;
						}

						if( writeSuccess )
						{
							// Even if we don't dequeue successfully we'll just write the same thing again which isn't a huge deal...
							// Hopefully in that case it just fixes itself later.
							do
							{
								dequeueSuccess = writeQueue.TryDequeue( out queuedWrite );
							}
							while( !dequeueSuccess && dequeueFailCounter++ <= 10 );
							dequeueFailCounter = 0;
						}
					}
					else if( peekFailCounter++ >= 10 )
					{
						// we appear to be having an unknown issue. try again later.
						peekFailCounter = 0;
						break;
					}
				}
			}
			writeEvent.Dispose();
			writeQueue.Clear();
			return;

WRITE_STREAM_FAILURE:
			unplug( joinInputThread: false );
			writeEvent.Dispose();
			writeQueue.Clear();
		}

		public override void unplug( bool joinInputThread = true )
		{
			// In general, errors also run this function, which can be called from multiple threads.
			// Therefore, make some effort to ensure we don't double-up on everything here.
			// The StadiEm control flow for exceptions is horrible; TODO: fix that.
			if( running )
			{
				running = false;
				// This seems out of order but it's what works.
				_stream.Dispose();

				if( pluggedIn360 )
				{
					pluggedIn360 = false;
					target360.Disconnect();
				}

				if( joinInputThread )
				{
					writeThread.Join();
					inputThread.Join();
				}
			}
		}

		private void input_thread()
		{
			bool ss_button_held = false;
			bool assistant_button_held = false;
			bool instant_trigger_release = true;
			bool instantReleaseL = false;
			bool instantReleaseR = false;
			_stream.ReadTimeout = Timeout.Infinite;
			byte[] data = new byte[_device.GetMaxInputReportLength()];

			StadiaReport report = new StadiaReport();
			StadiaReport report_prev = new StadiaReport();
			while( running )
			{
				int read = 0;
				try
				{
					// null checking _stream is useless because it can get closed while we're blocking on this read.
					read = _stream.Read( data );
				}
				catch( IOException e )
				{
					if( e.InnerException != null &&
						e.InnerException is Win32Exception exception &&
						( exception.NativeErrorCode.Equals( 0x0000048F ) || exception.NativeErrorCode.Equals( 0x000001B1 ) ) )
					{
						goto INPUT_STREAM_FAILURE;
					}
					else
					{
						throw e;
					}
				}
				catch( ObjectDisposedException )
				{
					goto INPUT_STREAM_FAILURE;
				}

				if( report.PopulateFromReport( data ) )
				{
					// stick deadzones
					// edit data report directly on deadzones in case the user specified deadzone is for hardware issues;
					// we don't want to reference defective/noisy values later.
					/*
					if( ( state.LX <= 0x7F && ( state.LX + stickDeadzones[0] >= 0x80 ) ) ||
						( state.LX >= 0x81 && ( state.LX - stickDeadzones[0] <= 0x80 ) ) )
					{
						state.LX = 0x80;
					}
					if( ( state.LY <= 0x7F && ( state.LY + stickDeadzones[1] >= 0x80 ) ) ||
						( state.LY >= 0x81 && ( state.LY - stickDeadzones[1] <= 0x80 ) ) )
					{
						state.LY = 0x80;
					}
					if( ( state.RX <= 0x7F && ( state.RX + stickDeadzones[2] >= 0x80 ) ) ||
						( state.RX >= 0x81 && ( state.RX - stickDeadzones[2] <= 0x80 ) ) )
					{
						state.RX = 0x80;
					}
					if( ( state.RY <= 0x7F && ( state.RY + stickDeadzones[3] >= 0x80 ) ) ||
						( state.RY >= 0x81 && ( state.RY - stickDeadzones[3] <= 0x80 ) ) )
					{
						state.RY = 0x80;
					}*/

					// trigger deadzones and instant-release feature
					/*
					if( state.L2 > 0x00 )
					{
						if( state.L2 - triggerDeadzones[0] <= 0x00 )
						{
							state.L2 = 0x00;
						}
						else if( state.L2 + triggerDeadzones[1] >= 0xFF )
						{
							state.L2 = 0xFF;
						}
					}
					if( state.R2 > 0x00 )
					{
						if( state.R2 - triggerDeadzones[2] <= 0x00 )
						{
							state.R2 = 0x00;
						}
						else if( state.R2 + triggerDeadzones[3] >= 0xFF )
						{
							state.R2 = 0xFF;
						}
					}*/

					byte curL2 = report.L2;
					byte curR2 = report.R2;
					if( instant_trigger_release )
					{
						if( !instantReleaseL )
						{
							if( report_prev.L2 == 0xFF && curL2 < 0xFF )
							{
								curL2 = 0x00;
								instantReleaseL = true;
							}
						}
						else if( curL2 == 0x00 || curL2 > report_prev.L2 )
						{
							instantReleaseL = false;
						}
						else // are currently instant releasing
						{
							curL2 = 0x00;
						}

						if( !instantReleaseR )
						{
							if( report_prev.R2 == 0xFF && curR2 < 0xFF )
							{
								curR2 = 0x00;
								instantReleaseR = true;
							}
						}
						else if( curR2 == 0x00 || curR2 > report_prev.R2 )
						{
							instantReleaseR = false;
						}
						else // are currently instant releasing
						{
							curR2 = 0x00;
						}
					}

					target360.SetButtonState( Xbox360Button.A, report.A );
					target360.SetButtonState( Xbox360Button.B, report.B );
					target360.SetButtonState( Xbox360Button.X, report.X );
					target360.SetButtonState( Xbox360Button.Y, report.Y );
					target360.SetButtonState( Xbox360Button.Up, report.Up );
					target360.SetButtonState( Xbox360Button.Down, report.Down );
					target360.SetButtonState( Xbox360Button.Left, report.Left );
					target360.SetButtonState( Xbox360Button.Right, report.Right );
					target360.SetButtonState( Xbox360Button.LeftShoulder, report.L1 );
					target360.SetButtonState( Xbox360Button.RightShoulder, report.R1 );
					target360.SetButtonState( Xbox360Button.LeftThumb, report.L3 );
					target360.SetButtonState( Xbox360Button.RightThumb, report.R3 );
					target360.SetButtonState( Xbox360Button.Back, report.Select );
					target360.SetButtonState( Xbox360Button.Start, report.Start );
					target360.SetButtonState( Xbox360Button.Guide, report.Stadia );

					target360.SetAxisValue( Xbox360Axis.LeftThumbX, report.LX );
					target360.SetAxisValue( Xbox360Axis.LeftThumbY, report.LY );
					target360.SetAxisValue( Xbox360Axis.RightThumbX, report.RX );
					target360.SetAxisValue( Xbox360Axis.RightThumbY, report.RY );

					target360.SetSliderValue( Xbox360Slider.LeftTrigger, curL2 );
					target360.SetSliderValue( Xbox360Slider.RightTrigger, curR2 );
					target360.SubmitReport();

					if( report.Screenshot && !ss_button_held )
					{
						ss_button_held = true;
						try
						{
							// TODO: Allow configuring this keybind.
							ssThread = new Thread( () => System.Windows.Forms.SendKeys.SendWait( "^+Z" ) );
							ssThread.Start();
						}
						catch
						{
						}
					}
					else if( ss_button_held && !report.Screenshot )
					{
						ss_button_held = false;
					}

					if( report.Assistant && !assistant_button_held )
					{
						assistant_button_held = true;
						try
						{
							// TODO: Allow configuring this keybind.
							vidThread = new Thread( () => System.Windows.Forms.SendKeys.SendWait( "^+E" ) );
							vidThread.Start();
						}
						catch
						{
						}
					}
					else if( assistant_button_held && !report.Assistant )
					{
						assistant_button_held = false;
					}

					report.CopyValuesTo( report_prev );
				}
			}
			return;

INPUT_STREAM_FAILURE:
			unplug( joinInputThread: false );
		}

		public class StadiaReport
		{
			public StadiaButton A = new StadiaButton( "A" );
			public StadiaButton B = new StadiaButton( "B" );
			public StadiaButton X = new StadiaButton( "X" );
			public StadiaButton Y = new StadiaButton( "Y" );
			public StadiaButton Up = new StadiaButton( "Up" );
			public StadiaButton Down = new StadiaButton( "Down" );
			public StadiaButton Left = new StadiaButton( "Left" );
			public StadiaButton Right = new StadiaButton( "Right" );
			public StadiaButton L1 = new StadiaButton( "L1" );
			public StadiaButton R1 = new StadiaButton( "R1" );
			public StadiaButton L3 = new StadiaButton( "L3" );
			public StadiaButton R3 = new StadiaButton( "R3" );
			public StadiaButton Assistant = new StadiaButton( "Assistant" );
			public StadiaButton Screenshot = new StadiaButton( "Screenshot" );
			public StadiaButton Select = new StadiaButton( "Select" );
			public StadiaButton Start = new StadiaButton( "Start" );
			public StadiaButton Stadia = new StadiaButton( "Stadia" );
			public StadiaAxis LX = new StadiaAxis( "LX", true );
			public StadiaAxis LY = new StadiaAxis( "LY", false );
			public StadiaAxis RX = new StadiaAxis( "RX", true );
			public StadiaAxis RY = new StadiaAxis( "RY", false );
			public StadiaSlider L2 = new StadiaSlider( "L2" );
			public StadiaSlider R2 = new StadiaSlider( "R2" );

			public StadiaReport()
			{
			}

			public bool PopulateFromReport( byte[] report )
			{
				// A newer firmware uses 11 byte outputs, format appears to be unchanged and I have not found what the extra byte actually is.
				if( ( report.Length == 11 || report.Length == 10 ) && report[DATA_ID] == 0x03 )
				{
					A.Value = ( report[DATA_BUTTONS_2] & 0x40 ) != 0;
					B.Value = ( report[DATA_BUTTONS_2] & 0x20 ) != 0;
					X.Value = ( report[DATA_BUTTONS_2] & 0x10 ) != 0;
					Y.Value = ( report[DATA_BUTTONS_2] & 0x08 ) != 0;
					L1.Value = ( report[DATA_BUTTONS_2] & 0x04 ) != 0;
					R1.Value = ( report[DATA_BUTTONS_2] & 0x02 ) != 0;
					L3.Value = ( report[DATA_BUTTONS_2] & 0x01 ) != 0;
					R3.Value = ( report[DATA_BUTTONS_1] & 0x80 ) != 0;
					Screenshot.Value = ( report[DATA_BUTTONS_1] & 0x01 ) != 0;
					Assistant.Value = ( report[DATA_BUTTONS_1] & 0x02 ) != 0;
					Start.Value = ( report[DATA_BUTTONS_1] & 0x20 ) != 0;
					Select.Value = ( report[DATA_BUTTONS_1] & 0x40 ) != 0;
					Stadia.Value = ( report[DATA_BUTTONS_1] & 0x10 ) != 0;
					LX.Value = report[DATA_LX];
					LY.Value = report[DATA_LY];
					RX.Value = report[DATA_RX];
					RY.Value = report[DATA_RY];
					L2.Value = report[DATA_L2];
					R2.Value = report[DATA_R2];

					switch( report[DATA_DPAD] )
					{
						case 8:
						default:
							Up.Value = Right.Value = Down.Value = Left.Value = false;
							break;
						case 0:
							Up.Value = true;
							Right.Value = Down.Value = Left.Value = false;
							break;
						case 1:
							Up.Value = Right.Value = true;
							Down.Value = Left.Value = false;
							break;
						case 2:
							Right.Value = true;
							Down.Value = Left.Value = Up.Value = false;
							break;
						case 3:
							Right.Value = Down.Value = true;
							Left.Value = Up.Value = false;
							break;
						case 4:
							Down.Value = true;
							Left.Value = Up.Value = Right.Value = false;
							break;
						case 5:
							Down.Value = Left.Value = true;
							Up.Value = Right.Value = false;
							break;
						case 6:
							Left.Value = true;
							Up.Value = Right.Value = Down.Value = false;
							break;
						case 7:
							Left.Value = Up.Value = true;
							Right.Value = Down.Value = false;
							break;
					}
					return true;
				}
				return false;
			}

			public void CopyValuesTo( StadiaReport other )
			{
				other.A.Value = A.Value;
				other.B.Value = B.Value;
				other.X.Value = X.Value;
				other.Y.Value = Y.Value;
				other.L1.Value = L1.Value;
				other.R1.Value = R1.Value;
				other.L3.Value = L3.Value;
				other.R3.Value = R3.Value;
				other.Screenshot.Value = Screenshot.Value;
				other.Assistant.Value = Assistant.Value;
				other.Start.Value = Start.Value;
				other.Select.Value = Select.Value;
				other.Stadia.Value = Stadia.Value;
				other.LX.Value = LX.Value;
				other.LY.Value = LY.Value;
				other.RX.Value = RX.Value;
				other.RY.Value = RY.Value;
				other.L2.Value = L2.Value;
				other.R2.Value = R2.Value;
			}
		}

		public abstract class StadiaProperty
		{
			public string Name;

			public StadiaProperty( string name )
			{
				this.Name = name;
			}
		}

		public class StadiaButton : StadiaProperty
		{
			public bool Value
			{
				get;
				set;
			}

			public StadiaButton( string name ) : base( name )
			{
			}

			public static implicit operator bool( StadiaButton b ) => b.Value;
		}

		public class StadiaAxis : StadiaProperty
		{
			public byte Value
			{
				get;
				set;
			}

			public bool IsXaxis
			{
				get;
			}

			public StadiaAxis( string name, bool isxaxis ) : base( name )
			{
				this.IsXaxis = isxaxis;
			}

			public static implicit operator byte( StadiaAxis a ) => a.Value;

			public static implicit operator short( StadiaAxis a )
			{
				byte input = a.Value;
				short ret;
				// Note: The HID reports do not allow stick values of 00.
				// This seems to make sense: 0x80 is center, so usable values are:
				// 0x01 to 0x7F and 0x81 to 0xFF.
				// For our purposes I believe this is undesirable. Subtract 1 from negative
				// values to allow maxing out the stick values.
				// TODO: Get an Xbox controller and verify this is standard behavior.
				if( input <= 0x7F && input > 0x00 )
				{
					input -= 0x01;
				}

				ushort stickUnsigned = (ushort)( input << 8 | ( input << 1 & 0xFF ) );
				if( stickUnsigned == 0xFFFE )
					stickUnsigned = 0xFFFF;

				if( a.IsXaxis )
				{
					ret = (short)( stickUnsigned - 0x8000 );
				}
				else
				{
					ret = (short)( -stickUnsigned + 0x7FFF );
					if( ret == -1 )
					{
						ret = 0;
					}
				}

				return ret;
			}
		}

		public class StadiaSlider : StadiaProperty
		{
			public byte Value
			{
				get;
				set;
			}

			public StadiaSlider( string name ) : base( name )
			{
			}

			public static implicit operator byte( StadiaSlider s ) => s.Value;
		}
	}
}
