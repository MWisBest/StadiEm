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

using System.Runtime.InteropServices;
using System.Windows.Forms;


static class KeyboardSend
{
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, int dwFlags, int dwExtraInfo);

    private const int KEYEVENTF_EXTENDEDKEY = 1;
    private const int KEYEVENTF_KEYUP = 2;

    public static void KeyDown(Keys vKey)
    {
        keybd_event((byte)vKey, 0, KEYEVENTF_EXTENDEDKEY, 0);
    }

    public static void KeyUp(Keys vKey)
    {
        keybd_event((byte)vKey, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, 0);
    }
}
namespace StadiEm.Device.Stadia
{
	public class StadiaController : BaseHIDController
	{
		public const ushort VID = 0x18D1;
		public const ushort PID = 0x9400;

		public List<Dictionary<Type, Xbox360Property[]>> profiles;
		public int currentProfile = 0;

		private Dictionary<Type, Xbox360Property[]> xboxMap;

		public Thread ssThread, vidThread, inputThread, writeThread;
		private AutoResetEvent writeEvent;
		private ConcurrentQueue<byte[]> writeQueue;

		public StadiaController( HidDevice device, HidStream stream, ViGEmClient client, int index ) : base( device, stream, client, index )
		{
			profiles = new List<Dictionary<Type, Xbox360Property[]>>();
			xboxMap = new Dictionary<Type, Xbox360Property[]>
			{
				[typeof( StadiaButton.A )] = new Xbox360Property[] { Xbox360Button.A },
				[typeof( StadiaButton.B )] = new Xbox360Property[] { Xbox360Button.B },
				[typeof( StadiaButton.X )] = new Xbox360Property[] { Xbox360Button.X },
				[typeof( StadiaButton.Y )] = new Xbox360Property[] { Xbox360Button.Y },
				[typeof( StadiaButton.Up )] = new Xbox360Property[] { Xbox360Button.Up },
				[typeof( StadiaButton.Down )] = new Xbox360Property[] { Xbox360Button.Down },
				[typeof( StadiaButton.Left )] = new Xbox360Property[] { Xbox360Button.Left },
				[typeof( StadiaButton.Right )] = new Xbox360Property[] { Xbox360Button.Right },
				[typeof( StadiaButton.L1 )] = new Xbox360Property[] { Xbox360Button.LeftShoulder },
				[typeof( StadiaButton.R1 )] = new Xbox360Property[] { Xbox360Button.RightShoulder },
				[typeof( StadiaButton.L3 )] = new Xbox360Property[] { Xbox360Button.LeftThumb },
				[typeof( StadiaButton.R3 )] = new Xbox360Property[] { Xbox360Button.RightThumb },
				[typeof( StadiaButton.Select )] = new Xbox360Property[] { Xbox360Button.Back },
				[typeof( StadiaButton.Start )] = new Xbox360Property[] { Xbox360Button.Start },
				[typeof( StadiaButton.Stadia )] = new Xbox360Property[] { Xbox360Button.Guide },
				[typeof( StadiaAxis.LX )] = new Xbox360Property[] { Xbox360Axis.LeftThumbX },
				[typeof( StadiaAxis.LY )] = new Xbox360Property[] { Xbox360Axis.LeftThumbY },
				[typeof( StadiaAxis.RX )] = new Xbox360Property[] { Xbox360Axis.RightThumbX },
				[typeof( StadiaAxis.RY )] = new Xbox360Property[] { Xbox360Axis.RightThumbY },
				[typeof( StadiaSlider.L2 )] = new Xbox360Property[] { Xbox360Slider.LeftTrigger },
				[typeof( StadiaSlider.R2 )] = new Xbox360Property[] { Xbox360Slider.RightTrigger },
			};
			Dictionary<Type, Xbox360Property[]> forzaMap = new Dictionary<Type, Xbox360Property[]>();
			foreach( Type key in xboxMap.Keys )
			{
				forzaMap.Add( key, xboxMap[key] );
			}
			forzaMap[typeof( StadiaButton.B )] = new Xbox360Property[] { Xbox360Button.B, Xbox360Button.A };
			forzaMap[typeof( StadiaButton.X )] = new Xbox360Property[] { Xbox360Button.X, Xbox360Button.A };
			forzaMap[typeof( StadiaButton.L1 )] = new Xbox360Property[] { Xbox360Button.LeftShoulder, Xbox360Button.A };
			profiles.Add( xboxMap );
			profiles.Add( forzaMap );

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
				writeEvent.WaitOne( 1000 );
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
			_stream.ReadTimeout = Timeout.Infinite;
			byte[] data = new byte[_device.GetMaxInputReportLength()];

			StadiaReport report = new StadiaReport();
			//report.L2.InstantRelease = true;
			//report.R2.InstantRelease = true;
			//report.L3.ToggleMode = true;
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

					// reset report in case profile updates as we're running
					target360.ResetReport();
					Dictionary<Type, Xbox360Property[]> profile = profiles[currentProfile];
					foreach( StadiaProperty prop in report.Props )
					{
						Type stadiaType = prop.GetType();
						if( profile.TryGetValue( stadiaType, out Xbox360Property[] xboxPropArray ) )
						{
							for( int i = 0; i < xboxPropArray.Length; ++i)
							{
								Xbox360Property xboxProp = xboxPropArray[i];
								if( xboxProp is Xbox360Button xbutton )
								{
									if( prop is StadiaButton sbutton )
									{
										if( i == 0 || sbutton )
											target360.SetButtonState( xbutton, sbutton );
									}
									else if( prop is StadiaSlider sslider )
									{
										target360.SetButtonState( xbutton, sslider );
									}
									else if( prop is StadiaAxis saxis )
									{
										target360.SetButtonState( xbutton, saxis );
									}
								}
								else if( xboxProp is Xbox360Slider xslider )
								{
									if( prop is StadiaSlider sslider )
									{
										target360.SetSliderValue( xslider, sslider );
									}
									else if( prop is StadiaButton sbutton )
									{
										target360.SetSliderValue( xslider, sbutton );
									}
									else if( prop is StadiaAxis saxis )
									{
										target360.SetSliderValue( xslider, saxis );
									}
								}
								else if( xboxProp is Xbox360Axis xaxis )
								{
									if( prop is StadiaAxis saxis )
									{
										target360.SetAxisValue( xaxis, saxis );
									}
									else
									{
										throw new NotImplementedException();
									}
								}
							}
						}
					}

					target360.SubmitReport();

					if( report.Screenshot.Pressed )
					{
						try
						{
							// Captures a screenshot of active window using Win+Alt+PrintScreen
							KeyboardSend.KeyDown(Keys.LWin);
							System.Windows.Forms.SendKeys.SendWait("%{PRTSC}");
							KeyboardSend.KeyUp(Keys.LWin);
						}
						catch
						{
						}
					}

					if( report.Assistant.Pressed )
					{
						try
						{
							// Records Last 30 Seconds using Win+Alt+G
							KeyboardSend.KeyDown(Keys.LWin);
							System.Windows.Forms.SendKeys.SendWait("%{g}");
							KeyboardSend.KeyUp(Keys.LWin);
						}
						catch
						{
						}
					}

					if( report.Stadia && ( report.Down.Pressed || report.Up.Pressed ) )
					{
						if( report.Up.Pressed )
						{
							if( ++currentProfile > profiles.Count - 1 )
							{
								currentProfile = profiles.Count - 1;
							}
						}
						else// if( report.Down.Pressed )
						{
							if( --currentProfile < 0 )
							{
								currentProfile = 0;
							}
						}
					}
				}
			}
			return;

INPUT_STREAM_FAILURE:
			unplug( joinInputThread: false );
		}

		public class StadiaReport
		{
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

			public StadiaProperty[] Props
			{
				get;
			}
			public StadiaButton.A A = new StadiaButton.A();
			public StadiaButton.B B = new StadiaButton.B();
			public StadiaButton.X X = new StadiaButton.X();
			public StadiaButton.Y Y = new StadiaButton.Y();
			public StadiaButton.Up Up = new StadiaButton.Up();
			public StadiaButton.Down Down = new StadiaButton.Down();
			public StadiaButton.Left Left = new StadiaButton.Left();
			public StadiaButton.Right Right = new StadiaButton.Right();
			public StadiaButton.L1 L1 = new StadiaButton.L1();
			public StadiaButton.R1 R1 = new StadiaButton.R1();
			public StadiaButton.L3 L3 = new StadiaButton.L3();
			public StadiaButton.R3 R3 = new StadiaButton.R3();
			public StadiaButton.Assistant Assistant = new StadiaButton.Assistant();
			public StadiaButton.Screenshot Screenshot = new StadiaButton.Screenshot();
			public StadiaButton.Select Select = new StadiaButton.Select();
			public StadiaButton.Start Start = new StadiaButton.Start();
			public StadiaButton.Stadia Stadia = new StadiaButton.Stadia();
			public StadiaAxis.LX LX = new StadiaAxis.LX();
			public StadiaAxis.LY LY = new StadiaAxis.LY();
			public StadiaAxis.RX RX = new StadiaAxis.RX();
			public StadiaAxis.RY RY = new StadiaAxis.RY();
			public StadiaSlider.L2 L2 = new StadiaSlider.L2();
			public StadiaSlider.R2 R2 = new StadiaSlider.R2();

			public StadiaReport()
			{
				Props = new StadiaProperty[]
				{
					A,
					B,
					X,
					Y,
					Up,
					Down,
					Left,
					Right,
					L1,
					R1,
					L3,
					R3,
					Assistant,
					Screenshot,
					Select,
					Start,
					Stadia,
					LX,
					LY,
					RX,
					RY,
					L2,
					R2
				};
			}

			public bool PopulateFromReport( byte[] report )
			{
				// Report length is supposed to be 10 or 11 bytes (newer fw is 11). Just check that we won't read out of bounds though.
				if( report.Length > DATA_R2 && report[DATA_ID] == 0x03 )
				{
					byte scratch = report[DATA_BUTTONS_1];
					Screenshot.Value = ( scratch & 0x01 ) > 0;
					Assistant.Value = ( scratch & 0x02 ) > 0;
					Stadia.Value = ( scratch & 0x10 ) > 0;
					Start.Value = ( scratch & 0x20 ) > 0;
					Select.Value = ( scratch & 0x40 ) > 0;
					R3.Value = ( scratch & 0x80 ) > 0;
					scratch = report[DATA_BUTTONS_2];
					L3.Value = ( scratch & 0x01 ) > 0;
					R1.Value = ( scratch & 0x02 ) > 0;
					L1.Value = ( scratch & 0x04 ) > 0;
					Y.Value = ( scratch & 0x08 ) > 0;
					X.Value = ( scratch & 0x10 ) > 0;
					B.Value = ( scratch & 0x20 ) > 0;
					A.Value = ( scratch & 0x40 ) > 0;
					LX.Value = report[DATA_LX];
					LY.Value = report[DATA_LY];
					RX.Value = report[DATA_RX];
					RY.Value = report[DATA_RY];
					L2.Value = report[DATA_L2];
					R2.Value = report[DATA_R2];

					switch( report[DATA_DPAD] )
					{
						default:
							Up.Value = Right.Value = Down.Value = Left.Value = false;
							return true;
						case 0:
							Up.Value = true;
							Right.Value = Down.Value = Left.Value = false;
							return true;
						case 1:
							Up.Value = Right.Value = true;
							Down.Value = Left.Value = false;
							return true;
						case 2:
							Right.Value = true;
							Down.Value = Left.Value = Up.Value = false;
							return true;
						case 3:
							Right.Value = Down.Value = true;
							Left.Value = Up.Value = false;
							return true;
						case 4:
							Down.Value = true;
							Left.Value = Up.Value = Right.Value = false;
							return true;
						case 5:
							Down.Value = Left.Value = true;
							Up.Value = Right.Value = false;
							return true;
						case 6:
							Left.Value = true;
							Up.Value = Right.Value = Down.Value = false;
							return true;
						case 7:
							Left.Value = Up.Value = true;
							Right.Value = Down.Value = false;
							return true;
					}
					//return true; // note: unreachable
				}
				return false;
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
			private bool _value = false;
			private bool _valueprev = false;
			private bool _valueraw = false;
			private bool _valuerawprev = false;
			private bool _toggleMode = false;

			public bool Value
			{
				get => _value;
				set
				{
					_valueprev = _value;
					_valuerawprev = _valueraw;
					_valueraw = value;
					if( !ToggleMode )
					{
						_value = value;
					}
					else if( value && value != _valuerawprev )
					{
						_value = !_value;
					}
				}
			}

			public bool ValuePrev
			{
				get => _valueprev;
				set => _valueprev = value;
			}

			public bool ValueRaw
			{
				get => _valueraw;
				set => _valueraw = value;
			}

			public bool ValueRawPrev
			{
				get => _valuerawprev;
				set => _valuerawprev = value;
			}

			public bool Pressed
			{
				get => _valueraw && !_valuerawprev;
			}

			public bool Released
			{
				get => !_valueraw && _valuerawprev;
			}

			public bool ToggleMode
			{
				get => _toggleMode;
				set => _toggleMode = value;
			}

			public StadiaButton( string name ) : base( name )
			{
			}

			public class A : StadiaButton
			{
				public A() : base( "A" )
				{
				}
			}
			public class B : StadiaButton
			{
				public B() : base( "B" )
				{
				}
			}
			public class X : StadiaButton
			{
				public X() : base( "X" )
				{
				}
			}
			public class Y : StadiaButton
			{
				public Y() : base( "Y" )
				{
				}
			}
			public class Up : StadiaButton
			{
				public Up() : base( "Up" )
				{
				}
			}
			public class Down : StadiaButton
			{
				public Down() : base( "Down" )
				{
				}
			}
			public class Left : StadiaButton
			{
				public Left() : base( "Left" )
				{
				}
			}
			public class Right : StadiaButton
			{
				public Right() : base( "Right" )
				{
				}
			}
			public class L1 : StadiaButton
			{
				public L1() : base( "L1" )
				{
				}
			}
			public class R1 : StadiaButton
			{
				public R1() : base( "R1" )
				{
				}
			}
			public class L3 : StadiaButton
			{
				public L3() : base( "L3" )
				{
				}
			}
			public class R3 : StadiaButton
			{
				public R3() : base( "R3" )
				{
				}
			}
			public class Assistant : StadiaButton
			{
				public Assistant() : base( "Assistant" )
				{
				}
			}
			public class Screenshot : StadiaButton
			{
				public Screenshot() : base( "Screenshot" )
				{
				}
			}
			public class Select : StadiaButton
			{
				public Select() : base( "Select" )
				{
				}
			}
			public class Start : StadiaButton
			{
				public Start() : base( "Start" )
				{
				}
			}
			public class Stadia : StadiaButton
			{
				public Stadia() : base( "Stadia" )
				{
				}
			}

			public static implicit operator bool( StadiaButton b ) => b.Value;

			public static implicit operator byte( StadiaButton b ) => b.Value ? byte.MaxValue : byte.MinValue;
		}

		public class StadiaAxis : StadiaProperty
		{
			private byte _value = 0x80;
			private byte _valueprev = 0x80;
			private byte _valueraw = 0x80;
			private byte _valuerawprev = 0x80;

			public byte Value
			{
				get => _value;
				set
				{
					_valuerawprev = _valueraw;
					_valueprev = _value;
					_valueraw = value;
					_value = value;
				}
			}

			public byte ValuePrev
			{
				get => _valueprev;
				set => _valueprev = value;
			}

			public byte ValueRaw
			{
				get => _valueraw;
				set => _valueraw = value;
			}

			public byte ValueRawPrev
			{
				get => _valuerawprev;
				set => _valuerawprev = value;
			}

			public bool IsXaxis
			{
				get;
			}

			public StadiaAxis( string name, bool isxaxis ) : base( name )
			{
				this.IsXaxis = isxaxis;
			}

			public class LX : StadiaAxis
			{
				public LX() : base( "LX", true )
				{
				}
			}
			public class LY : StadiaAxis
			{
				public LY() : base( "LY", false )
				{
				}
			}
			public class RX : StadiaAxis
			{
				public RX() : base( "RX", true )
				{
				}
			}
			public class RY : StadiaAxis
			{
				public RY() : base( "RY", false )
				{
				}
			}

			public static implicit operator byte( StadiaAxis a ) => a.Value;

			// TODO: Configure this.
			public static implicit operator bool( StadiaAxis a ) => ( Math.Abs(a.Value - 0x80) > 0x40 );

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

				ushort stickUnsigned = (ushort)( input << 8 | ( ( input << 1 ) & 0xFF ) );
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
			private byte _value = 0x00;
			private byte _valueprev = 0x00;
			private byte _valueraw = 0x00;
			private byte _valuerawprev = 0x00;
			private bool _instantrelease = false;
			private bool _togglemode = false;
			private bool areInstantReleasing = false;
			private bool toggleState = false;

			public byte Value
			{
				get => _value;
				set
				{
					_valuerawprev = _valueraw;
					_valueprev = _value;
					_valueraw = value;
					if( !_instantrelease && !_togglemode )
					{
						_value = value;
						return;
					}

					if( _instantrelease )
					{
						if( value == 0x00 || value > _valuerawprev )
						{
							_value = value;
							areInstantReleasing = false;
						}
						else if( !areInstantReleasing )
						{
							if( _valuerawprev == 0xFF && value < 0xFF )
							{
								_value = 0x00;
								areInstantReleasing = true;
							}
							else
							{
								_value = value;
							}
						}
						else
						{
							_value = 0x00;
						}
					}
					else if( _togglemode )
					{
						if( value == 0xFF && _valuerawprev < 0xFF )
						{
							toggleState = !toggleState;
						}
						_value = (byte)( toggleState ? 0xFF : 0x00 );
					}
				}
			}

			public byte ValuePrev
			{
				get => _valueprev;
				set => _valueprev = value;
			}

			public byte ValueRaw
			{
				get => _valueraw;
				set => _valueraw = value;
			}

			public byte ValueRawPrev
			{
				get => _valuerawprev;
				set => _valuerawprev = value;
			}

			public bool InstantRelease
			{
				get => _instantrelease;
				set => _instantrelease = value;
			}

			public bool ToggleMode
			{
				get => _togglemode;
				set => _togglemode = value;
			}

			public StadiaSlider( string name ) : base( name )
			{
			}

			public class L2 : StadiaSlider
			{
				public L2() : base( "L2" )
				{
				}
			}
			public class R2 : StadiaSlider
			{
				public R2() : base( "R2" )
				{
				}
			}

			public static implicit operator byte( StadiaSlider s ) => s.Value;

			// TODO: Add different modes for slider to bool/button conversion
			public static implicit operator bool( StadiaSlider s ) => s.Value >= 0x40;
		}
	}
}
