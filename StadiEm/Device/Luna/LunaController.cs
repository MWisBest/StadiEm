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

namespace StadiEm.Device.Luna
{
	public class LunaController : BaseHIDController
	{
		public const ushort VID = 0x0171;
		public const ushort PID = 0x0419;

		public List<Dictionary<Type, Xbox360Property>> profiles;
		public int currentProfile = 0;

		private Dictionary<Type, Xbox360Property> xboxMap;

		public Thread ssThread, vidThread, inputThread, writeThread;
		private AutoResetEvent writeEvent;
		private ConcurrentQueue<byte[]> writeQueue;

		public LunaController( HidDevice device, HidStream stream, ViGEmClient client, int index ) : base( device, stream, client, index )
		{
			profiles = new List<Dictionary<Type, Xbox360Property>>();
			xboxMap = new Dictionary<Type, Xbox360Property>
			{
				[typeof( LunaButton.A )] = Xbox360Button.A,
				[typeof( LunaButton.B )] = Xbox360Button.B,
				[typeof( LunaButton.X )] = Xbox360Button.X,
				[typeof( LunaButton.Y )] = Xbox360Button.Y,
				[typeof( LunaButton.Up )] = Xbox360Button.Up,
				[typeof( LunaButton.Down )] = Xbox360Button.Down,
				[typeof( LunaButton.Left )] = Xbox360Button.Left,
				[typeof( LunaButton.Right )] = Xbox360Button.Right,
				[typeof( LunaButton.L1 )] = Xbox360Button.LeftShoulder,
				[typeof( LunaButton.R1 )] = Xbox360Button.RightShoulder,
				[typeof( LunaButton.L3 )] = Xbox360Button.LeftThumb,
				[typeof( LunaButton.R3 )] = Xbox360Button.RightThumb,
				[typeof( LunaButton.Select )] = Xbox360Button.Back,
				[typeof( LunaButton.Start )] = Xbox360Button.Start,
				[typeof( LunaButton.Luna )] = Xbox360Button.Guide,
				[typeof( LunaAxis.LX )] = Xbox360Axis.LeftThumbX,
				[typeof( LunaAxis.LY )] = Xbox360Axis.LeftThumbY,
				[typeof( LunaAxis.RX )] = Xbox360Axis.RightThumbX,
				[typeof( LunaAxis.RY )] = Xbox360Axis.RightThumbY,
				[typeof( LunaSlider.L2 )] = Xbox360Slider.LeftTrigger,
				[typeof( LunaSlider.R2 )] = Xbox360Slider.RightTrigger,
			};
			Dictionary<Type, Xbox360Property> codMap = new Dictionary<Type, Xbox360Property>();
			foreach( Type key in xboxMap.Keys )
			{
				codMap.Add( key, xboxMap[key] );
			}
			codMap[typeof( LunaButton.B )] = Xbox360Slider.RightTrigger;
			codMap[typeof( LunaSlider.R2 )] = Xbox360Button.B;
			profiles.Add( xboxMap );
			profiles.Add( codMap );

			// Writes are currently broken because Windows hates ~~me~~ everyone
			//target360.FeedbackReceived += this.Target360_FeedbackReceived;
			//targetDS4.FeedbackReceived += this.TargetDS4_FeedbackReceived;

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
			// ??
			byte[] vibReport = { 0x09, 0x01, 0x08, 0x00, largeMotor, smallMotor, 0x00, 0x00, 0x00 };

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

			LunaReport report = new LunaReport();
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
					Dictionary<Type, Xbox360Property> profile = profiles[currentProfile];
					foreach( LunaProperty prop in report.Props )
					{
						Type stadiaType = prop.GetType();
						if( profile.TryGetValue( stadiaType, out Xbox360Property xboxProp ) )
						{
							if( xboxProp is Xbox360Button xbutton )
							{
								if( prop is LunaButton sbutton )
								{
									target360.SetButtonState( xbutton, sbutton );
								}
								else if( prop is LunaSlider sslider )
								{
									target360.SetButtonState( xbutton, sslider );
								}
								else if( prop is LunaAxis saxis )
								{
									target360.SetButtonState( xbutton, saxis );
								}
							}
							else if( xboxProp is Xbox360Slider xslider )
							{
								if( prop is LunaSlider sslider )
								{
									target360.SetSliderValue( xslider, sslider );
								}
								else if( prop is LunaButton sbutton )
								{
									target360.SetSliderValue( xslider, sbutton );
								}
								else if( prop is LunaAxis saxis )
								{
									target360.SetSliderValue( xslider, saxis );
								}
							}
							else if( xboxProp is Xbox360Axis xaxis )
							{
								if( prop is LunaAxis saxis )
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

					target360.SubmitReport();

					// Just use as screenshot for now I guess.
					if( report.Mic.Pressed )
					{
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

					if( report.Luna && ( report.Down.Pressed || report.Up.Pressed ) )
					{
						if( report.Up.Pressed )
						{
							if( ++currentProfile > profiles.Count - 1 )
							{
								currentProfile = profiles.Count - 1;
							}
						}
						else// if( report.Down )
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

		public class LunaReport
		{
			public const int DATA_ID = 0x00;
			public const int DATA_LX1 = 0x01;
			public const int DATA_LX2 = 0x02;
			public const int DATA_LY1 = 0x03;
			public const int DATA_LY2 = 0x04;
			public const int DATA_RX1 = 0x05;
			public const int DATA_RX2 = 0x06;
			public const int DATA_RY1 = 0x07;
			public const int DATA_RY2 = 0x08;
			public const int DATA_L2_LOW = 0x09;
			public const int DATA_L2_HIGH = 0x0A;
			public const int DATA_R2_LOW = 0x0B;
			public const int DATA_R2_HIGH = 0x0C;
			public const int DATA_DPAD = 0x0D;
			public const int DATA_BUTTONS_1 = 0x0E;
			public const int DATA_BUTTONS_2 = 0x0F;
			public const int DATA_BUTTONS_3 = 0x10;

			public LunaProperty[] Props
			{
				get;
			}
			public LunaButton.A A = new LunaButton.A();
			public LunaButton.B B = new LunaButton.B();
			public LunaButton.X X = new LunaButton.X();
			public LunaButton.Y Y = new LunaButton.Y();
			public LunaButton.Up Up = new LunaButton.Up();
			public LunaButton.Down Down = new LunaButton.Down();
			public LunaButton.Left Left = new LunaButton.Left();
			public LunaButton.Right Right = new LunaButton.Right();
			public LunaButton.L1 L1 = new LunaButton.L1();
			public LunaButton.R1 R1 = new LunaButton.R1();
			public LunaButton.L3 L3 = new LunaButton.L3();
			public LunaButton.R3 R3 = new LunaButton.R3();
			public LunaButton.Mic Mic = new LunaButton.Mic();
			public LunaButton.Select Select = new LunaButton.Select();
			public LunaButton.Start Start = new LunaButton.Start();
			public LunaButton.Luna Luna = new LunaButton.Luna();
			public LunaAxis.LX LX = new LunaAxis.LX();
			public LunaAxis.LY LY = new LunaAxis.LY();
			public LunaAxis.RX RX = new LunaAxis.RX();
			public LunaAxis.RY RY = new LunaAxis.RY();
			public LunaSlider.L2 L2 = new LunaSlider.L2();
			public LunaSlider.R2 R2 = new LunaSlider.R2();

			public LunaReport()
			{
				Props = new LunaProperty[]
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
					Mic,
					Select,
					Start,
					Luna,
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
				// NOTICE: Due to what I assume is a firmware bug, when the Luna button is pressed data[0] becomes 2 and data[1] (LX1) becomes 1.
				// The sticks are clearly 8 bit values repeated twice, so we treat them as a byte and handle the edge case for Luna button.
				if( report.Length > 16 && (report[DATA_ID] == 0x01 || (report[DATA_ID] == 0x02 && report[DATA_LX1] == 0x01)))
				{
					Luna.Value = ( report[DATA_ID] & 0x02 ) > 0;

					byte scratch = report[DATA_BUTTONS_1];
					A.Value = ( scratch & 0x01 ) > 0;
					B.Value = ( scratch & 0x02 ) > 0;
					X.Value = ( scratch & 0x08 ) > 0;
					Y.Value = ( scratch & 0x10 ) > 0;
					L1.Value = ( scratch & 0x40 ) > 0;
					R1.Value = ( scratch & 0x80 ) > 0;

					scratch = report[DATA_BUTTONS_2];
					Start.Value = ( scratch & 0x08 ) > 0;
					L3.Value = ( scratch & 0x20 ) > 0;
					R3.Value = ( scratch & 0x40 ) > 0;

					scratch = report[DATA_BUTTONS_3];
					Select.Value = ( scratch & 0x01 ) > 0;
					Mic.Value = ( scratch & 0x02 ) > 0;

					LX.Value = report[DATA_LX2];
					LY.Value = report[DATA_LY2];
					RX.Value = report[DATA_RX2];
					RY.Value = report[DATA_RY2];

					// L2/R2 are reported as 10-bit values.
					// Convert it down to 8-bit simply because no API we hand off to does anything more.
					// There appears to be a built in deadzone of about 100 (of 1023) on these values.
					L2.Value = (byte)( ( report[DATA_L2_HIGH] << 8 | report[DATA_L2_LOW] ) >> 2 );
					R2.Value = (byte)( ( report[DATA_R2_HIGH] << 8 | report[DATA_R2_LOW] ) >> 2 );

					switch( report[DATA_DPAD] )
					{
						default:
							Up.Value = Right.Value = Down.Value = Left.Value = false;
							return true;
						case 1:
							Up.Value = true;
							Right.Value = Down.Value = Left.Value = false;
							return true;
						case 2:
							Up.Value = Right.Value = true;
							Down.Value = Left.Value = false;
							return true;
						case 3:
							Right.Value = true;
							Down.Value = Left.Value = Up.Value = false;
							return true;
						case 4:
							Right.Value = Down.Value = true;
							Left.Value = Up.Value = false;
							return true;
						case 5:
							Down.Value = true;
							Left.Value = Up.Value = Right.Value = false;
							return true;
						case 6:
							Down.Value = Left.Value = true;
							Up.Value = Right.Value = false;
							return true;
						case 7:
							Left.Value = true;
							Up.Value = Right.Value = Down.Value = false;
							return true;
						case 8:
							Left.Value = Up.Value = true;
							Right.Value = Down.Value = false;
							return true;
					}
					//return true; // note: unreachable
				}
				return false;
			}
		}

		public abstract class LunaProperty
		{
			public string Name;

			public LunaProperty( string name )
			{
				this.Name = name;
			}
		}

		public class LunaButton : LunaProperty
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

			public LunaButton( string name ) : base( name )
			{
			}

			public class A : LunaButton
			{
				public A() : base( "A" )
				{
				}
			}
			public class B : LunaButton
			{
				public B() : base( "B" )
				{
				}
			}
			public class X : LunaButton
			{
				public X() : base( "X" )
				{
				}
			}
			public class Y : LunaButton
			{
				public Y() : base( "Y" )
				{
				}
			}
			public class Up : LunaButton
			{
				public Up() : base( "Up" )
				{
				}
			}
			public class Down : LunaButton
			{
				public Down() : base( "Down" )
				{
				}
			}
			public class Left : LunaButton
			{
				public Left() : base( "Left" )
				{
				}
			}
			public class Right : LunaButton
			{
				public Right() : base( "Right" )
				{
				}
			}
			public class L1 : LunaButton
			{
				public L1() : base( "L1" )
				{
				}
			}
			public class R1 : LunaButton
			{
				public R1() : base( "R1" )
				{
				}
			}
			public class L3 : LunaButton
			{
				public L3() : base( "L3" )
				{
				}
			}
			public class R3 : LunaButton
			{
				public R3() : base( "R3" )
				{
				}
			}
			public class Mic : LunaButton
			{
				public Mic() : base( "Mic" )
				{
				}
			}
			public class Select : LunaButton
			{
				public Select() : base( "Select" )
				{
				}
			}
			public class Start : LunaButton
			{
				public Start() : base( "Start" )
				{
				}
			}
			public class Luna : LunaButton
			{
				public Luna() : base( "Luna" )
				{
				}
			}

			public static implicit operator bool( LunaButton b ) => b.Value;

			public static implicit operator byte( LunaButton b ) => b.Value ? byte.MaxValue : byte.MinValue;
		}

		public class LunaAxis : LunaProperty
		{
			private byte _value = 0x7F;
			private byte _valueprev = 0x7F;
			private byte _valueraw = 0x7F;
			private byte _valuerawprev = 0x7F;

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

			public LunaAxis( string name, bool isxaxis ) : base( name )
			{
				this.IsXaxis = isxaxis;
			}

			public class LX : LunaAxis
			{
				public LX() : base( "LX", true )
				{
				}
			}
			public class LY : LunaAxis
			{
				public LY() : base( "LY", false )
				{
				}
			}
			public class RX : LunaAxis
			{
				public RX() : base( "RX", true )
				{
				}
			}
			public class RY : LunaAxis
			{
				public RY() : base( "RY", false )
				{
				}
			}

			public static implicit operator byte( LunaAxis a ) => a.Value;

			// TODO: Configure this.
			public static implicit operator bool( LunaAxis a ) => ( Math.Abs( a.Value - 0x80 ) > 0x40 );

			public static implicit operator short( LunaAxis a )
			{
				byte input = a.Value;
				short ret;
				// Axis values on Luna seem to be nonsense.
				// 0x7F is center
				// 0x00 - 0x7E is one end of readings
				// 0x80 - 0xFF is another end of readings
				// So for whatever reason the readings are unbalanced.
				// In addition, 0x80 does not seem to be achieveable on all axis,
				// and is more consistent on some axis than others when it is achieveable.
				// The best solution I can think of is to treat both 0x7F and 0x80 as 0.
				// TODO: Should this be handled on report generation? In .Value?? ????????
				if( input == 0x7F )
				{
					input = 0x80;
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

		public class LunaSlider : LunaProperty
		{
			private byte _value = 0x00;
			private byte _valueprev = 0x00;
			private byte _valueraw = 0x00;
			private byte _valuerawprev = 0x00;
			private bool _instantrelease = false;
			private bool areInstantReleasing = false;

			public byte Value
			{
				get => _value;
				set
				{
					_valuerawprev = _valueraw;
					_valueprev = _value;
					_valueraw = value;
					if( !_instantrelease )
					{
						_value = value;
					}
					else
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

			public LunaSlider( string name ) : base( name )
			{
			}

			public class L2 : LunaSlider
			{
				public L2() : base( "L2" )
				{
				}
			}
			public class R2 : LunaSlider
			{
				public R2() : base( "R2" )
				{
				}
			}

			public static implicit operator byte( LunaSlider s ) => s.Value;

			// TODO: Add different modes for slider to bool/button conversion
			public static implicit operator bool( LunaSlider s ) => s.Value >= 0x40;
		}
	}
}
