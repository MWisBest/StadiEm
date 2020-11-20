using HidSharp;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Threading;

namespace StadiEm
{
	class StadiaController : BaseHIDController
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
		public const int DATA_ZL = 0x08;
		public const int DATA_ZR = 0x09;

		public Thread ssThread, vidThread, inputThread, writeThread;
		public byte[] stickDeadzones;
		public byte[] triggerDeadzones;
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

			stickDeadzones = new byte[] { 1, 1, 1, 1 };
			triggerDeadzones = new byte[] { 1, 1 };

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
			bool ss_button_pressed = false;
			bool ss_button_held = false;
			bool assistant_button_pressed = false;
			bool assistant_button_held = false;
			bool useAssistantButtonAsGuide = false;
			_stream.ReadTimeout = Timeout.Infinite;
			byte[] data = new byte[_device.GetMaxInputReportLength()];
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

				if( read > 0 )
				{
					// A newer firmware uses 11 byte outputs, format appears to be unchanged and I have not found what the extra byte actually is.
					if( data[DATA_ID] == 0x03 && ( read == 10 || read == 11 ) )
					{
						target360.ResetReport();
						if( ( data[DATA_BUTTONS_2] & 64 ) != 0 )
							target360.SetButtonState( Xbox360Button.A, true );
						if( ( data[DATA_BUTTONS_2] & 32 ) != 0 )
							target360.SetButtonState( Xbox360Button.B, true );
						if( ( data[DATA_BUTTONS_2] & 16 ) != 0 )
							target360.SetButtonState( Xbox360Button.X, true );
						if( ( data[DATA_BUTTONS_2] & 8 ) != 0 )
							target360.SetButtonState( Xbox360Button.Y, true );
						if( ( data[DATA_BUTTONS_2] & 4 ) != 0 )
							target360.SetButtonState( Xbox360Button.LeftShoulder, true );
						if( ( data[DATA_BUTTONS_2] & 2 ) != 0 )
							target360.SetButtonState( Xbox360Button.RightShoulder, true );
						if( ( data[DATA_BUTTONS_2] & 1 ) != 0 )
							target360.SetButtonState( Xbox360Button.LeftThumb, true );
						if( ( data[DATA_BUTTONS_1] & 128 ) != 0 )
							target360.SetButtonState( Xbox360Button.RightThumb, true );
						ss_button_pressed = ( data[DATA_BUTTONS_1] & 1 ) != 0;
						assistant_button_pressed = ( data[DATA_BUTTONS_1] & 2 ) != 0;
						// [2] & 2 == Assistant, [2] & 1 == Screenshot

						bool up = false;
						bool down = false;
						bool left = false;
						bool right = false;

						switch( data[DATA_DPAD] )
						{
							case 8:
							default:
								break;
							case 0:
								up = true;
								break;
							case 1:
								up = true;
								right = true;
								break;
							case 2:
								right = true;
								break;
							case 3:
								down = true;
								right = true;
								break;
							case 4:
								down = true;
								break;
							case 5:
								down = true;
								left = true;
								break;
							case 6:
								left = true;
								break;
							case 7:
								up = true;
								left = true;
								break;
						}

						target360.SetButtonState( Xbox360Button.Up, up );
						target360.SetButtonState( Xbox360Button.Down, down );
						target360.SetButtonState( Xbox360Button.Left, left );
						target360.SetButtonState( Xbox360Button.Right, right );

						if( ( data[DATA_BUTTONS_1] & 32 ) != 0 )
							target360.SetButtonState( Xbox360Button.Start, true );
						if( ( data[DATA_BUTTONS_1] & 64 ) != 0 )
							target360.SetButtonState( Xbox360Button.Back, true );

						if( useAssistantButtonAsGuide && ( data[DATA_BUTTONS_1] & 2 ) != 0 )
						{
							target360.SetButtonState( Xbox360Button.Guide, true );
						}
						else if( ( data[DATA_BUTTONS_1] & 16 ) != 0 )
						{
							target360.SetButtonState( Xbox360Button.Guide, true );
						}

						// stick deadzones
						for( int i = DATA_LX; i <= DATA_RY; ++i )
						{
							if( ( data[i] <= 0x7F && ( data[i] + stickDeadzones[i - 4] >= 0x80 ) ) || ( data[i] >= 0x81 && ( data[i] - stickDeadzones[i - 4] <= 0x80) ) )
							{
								data[i] = 0x80;
							}
						}
						// trigger deadzones
						for( int i = DATA_ZL; i <= DATA_ZR; ++i )
						{
							if( data[i] > 0x00 && ( data[i] - triggerDeadzones[i - 8] <= 0x00 ) )
							{
								data[i] = 0x00;
							}
						}

						// Note: The HID reports do not allow stick values of 00.
						// This seems to make sense: 0x80 is center, so usable values are:
						// 0x01 to 0x7F and 0x81 to 0xFF.
						// For our purposes I believe this is undesirable. Subtract 1 from negative
						// values to allow maxing out the stick values.
						// TODO: Get an Xbox controller and verify this is standard behavior.
						for( int i = DATA_LX; i <= DATA_RY; ++i )
						{
							if( data[i] <= 0x7F && data[i] > 0x00 )
							{
								data[i] -= 0x01;
							}
						}

						ushort LeftStickXunsigned = (ushort)( data[DATA_LX] << 8 | ( data[DATA_LX] << 1 & 255 ) );
						if( LeftStickXunsigned == 0xFFFE )
							LeftStickXunsigned = 0xFFFF;
						short LeftStickX = (short)( LeftStickXunsigned - 0x8000 );

						ushort LeftStickYunsigned = (ushort)( data[DATA_LY] << 8 | ( data[DATA_LY] << 1 & 255 ) );
						if( LeftStickYunsigned == 0xFFFE )
							LeftStickYunsigned = 0xFFFF;
						short LeftStickY = (short)( -LeftStickYunsigned + 0x7FFF );
						if( LeftStickY == -1 )
							LeftStickY = 0;

						ushort RightStickXunsigned = (ushort)( data[DATA_RX] << 8 | ( data[DATA_RX] << 1 & 255 ) );
						if( RightStickXunsigned == 0xFFFE )
							RightStickXunsigned = 0xFFFF;
						short RightStickX = (short)( RightStickXunsigned - 0x8000 );

						ushort RightStickYunsigned = (ushort)( data[DATA_RY] << 8 | ( data[DATA_RY] << 1 & 255 ) );
						if( RightStickYunsigned == 0xFFFE )
							RightStickYunsigned = 0xFFFF;
						short RightStickY = (short)( -RightStickYunsigned + 0x7FFF );
						if( RightStickY == -1 )
							RightStickY = 0;

						target360.SetAxisValue( Xbox360Axis.LeftThumbX, LeftStickX );
						target360.SetAxisValue( Xbox360Axis.LeftThumbY, LeftStickY );
						target360.SetAxisValue( Xbox360Axis.RightThumbX, RightStickX );
						target360.SetAxisValue( Xbox360Axis.RightThumbY, RightStickY );
						target360.SetSliderValue( Xbox360Slider.LeftTrigger, data[DATA_ZL] );
						target360.SetSliderValue( Xbox360Slider.RightTrigger, data[DATA_ZR] );
						target360.SubmitReport();

						if( ss_button_pressed && !ss_button_held )
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
						else if( ss_button_held && !ss_button_pressed )
						{
							ss_button_held = false;
						}

						if( assistant_button_pressed && !assistant_button_held )
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
						else if( assistant_button_held && !assistant_button_pressed )
						{
							assistant_button_held = false;
						}
					}
				}
			}
			return;

INPUT_STREAM_FAILURE:
			unplug( joinInputThread: false );
		}
	}
}
