using HidSharp;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using System;
using System.IO;
using System.Threading;

namespace StadiEm
{
	class StadiaController
	{
		public const ushort VID = 0x18D1;
		public const ushort PID = 0x9400;
		public HidDevice _device;
		public HidStream _stream;
		public int _index;
		public bool running;
		public ViGEmClient _client;
		public IXbox360Controller target360;
		public IDualShock4Controller targetDS4;
		public Thread ssThread, inputThread;

		public StadiaController( HidDevice device, HidStream stream, ViGEmClient client, int index )
		{
			_device = device;
			_stream = stream;
			_client = client;
			_index = index;
			running = true;

			target360 = _client.CreateXbox360Controller();
			targetDS4 = _client.CreateDualShock4Controller();
			target360.AutoSubmitReport = false;
			targetDS4.AutoSubmitReport = false;
			target360.FeedbackReceived += this.Target360_FeedbackReceived;
			targetDS4.FeedbackReceived += this.TargetDS4_FeedbackReceived;

			inputThread = new Thread( () => input_thread() );
			inputThread.Priority = ThreadPriority.AboveNormal;
			inputThread.Name = "Controller #" + index + " Input";
			inputThread.Start();
		}

		private void Target360_FeedbackReceived( object sender, Xbox360FeedbackReceivedEventArgs e )
		{
			byte[] vibReport = { 0x05, e.LargeMotor, e.LargeMotor, e.SmallMotor, e.SmallMotor };
			_stream.Write( vibReport );
		}

		private void TargetDS4_FeedbackReceived( object sender, DualShock4FeedbackReceivedEventArgs e )
		{
			byte[] vibReport = { 0x05, e.LargeMotor, e.LargeMotor, e.SmallMotor, e.SmallMotor };
			_stream.Write( vibReport );
		}

		public void unplug( bool joinInputThread = true )
		{
			running = false;
			// This seems out of order but it's what works.
			try
			{
				_stream.Close();
			}
			catch
			{
				// ¯\_(ツ)_/¯
			}

			if( joinInputThread )
				inputThread.Join();
		}

		private void input_thread()
		{
			target360.Connect();
			bool ss_button_pressed = false;
			bool ss_button_held = false;
			_stream.ReadTimeout = Timeout.Infinite;
			byte[] data = new byte[_device.GetMaxInputReportLength()];
			while( running )
			{
				int read = 0;
				try
				{
					read = _stream.Read( data );
				}
				catch( TimeoutException e )
				{
					read = 0;
				}
				catch
				{
					unplug( joinInputThread: false );
				}

				if( read > 0 )
				{
					if( data[0] == 0x03 && read == 10 )
					{
						target360.ResetReport();
						if( ( data[3] & 64 ) != 0 )
							target360.SetButtonState( Xbox360Button.A, true );
						if( ( data[3] & 32 ) != 0 )
							target360.SetButtonState( Xbox360Button.B, true );
						if( ( data[3] & 16 ) != 0 )
							target360.SetButtonState( Xbox360Button.X, true );
						if( ( data[3] & 8 ) != 0 )
							target360.SetButtonState( Xbox360Button.Y, true );
						if( ( data[3] & 4 ) != 0 )
							target360.SetButtonState( Xbox360Button.LeftShoulder, true );
						if( ( data[3] & 2 ) != 0 )
							target360.SetButtonState( Xbox360Button.RightShoulder, true );
						if( ( data[3] & 1 ) != 0 )
							target360.SetButtonState( Xbox360Button.LeftThumb, true );
						if( ( data[2] & 128 ) != 0 )
							target360.SetButtonState( Xbox360Button.RightThumb, true );
						ss_button_pressed = ( data[2] & 1 ) != 0;
						//assistant_button_pressed = ( currentState[2] & 2 ) != 0;
						// [2] & 2 == Assistant, [2] & 1 == Screenshot

						switch( data[1] )
						{
							default:
								break;
							case 0:
								target360.SetButtonState( Xbox360Button.Up, true );
								break;
							case 1:
								target360.SetButtonState( Xbox360Button.Up, true );
								target360.SetButtonState( Xbox360Button.Right, true );
								break;
							case 2:
								target360.SetButtonState( Xbox360Button.Right, true );
								break;
							case 3:
								target360.SetButtonState( Xbox360Button.Down, true );
								target360.SetButtonState( Xbox360Button.Right, true );
								break;
							case 4:
								target360.SetButtonState( Xbox360Button.Down, true );
								break;
							case 5:
								target360.SetButtonState( Xbox360Button.Down, true );
								target360.SetButtonState( Xbox360Button.Left, true );
								break;
							case 6:
								target360.SetButtonState( Xbox360Button.Left, true );
								break;
							case 7:
								target360.SetButtonState( Xbox360Button.Up, true );
								target360.SetButtonState( Xbox360Button.Left, true );
								break;
						}

						if( ( data[2] & 32 ) != 0 )
							target360.SetButtonState( Xbox360Button.Start, true );
						if( ( data[2] & 64 ) != 0 )
							target360.SetButtonState( Xbox360Button.Back, true );

						if( ( data[2] & 16 ) != 0 )
						{
							target360.SetButtonState( Xbox360Button.Guide, true );
						}

						// Note: The HID reports do not allow stick values of 00.
						// This seems to make sense: 0x80 is center, so usable values are:
						// 0x01 to 0x7F and 0x81 to 0xFF.
						// For our purposes I believe this is undesirable. Subtract 1 from negative
						// values to allow maxing out the stick values.
						// TODO: Get an Xbox controller and verify this is standard behavior.
						for( int i = 4; i <= 7; ++i )
						{
							if( data[i] <= 0x7F && data[i] > 0x00 )
							{
								data[i] -= 0x01;
							}
						}

						ushort LeftStickXunsigned = (ushort)( data[4] << 8 | ( data[4] << 1 & 255 ) );
						if( LeftStickXunsigned == 0xFFFE )
							LeftStickXunsigned = 0xFFFF;
						short LeftStickX = (short)( LeftStickXunsigned - 0x8000 );

						ushort LeftStickYunsigned = (ushort)( data[5] << 8 | ( data[5] << 1 & 255 ) );
						if( LeftStickYunsigned == 0xFFFE )
							LeftStickYunsigned = 0xFFFF;
						short LeftStickY = (short)( -LeftStickYunsigned + 0x7FFF );
						if( LeftStickY == -1 )
							LeftStickY = 0;

						ushort RightStickXunsigned = (ushort)( data[6] << 8 | ( data[6] << 1 & 255 ) );
						if( RightStickXunsigned == 0xFFFE )
							RightStickXunsigned = 0xFFFF;
						short RightStickX = (short)( RightStickXunsigned - 0x8000 );

						ushort RightStickYunsigned = (ushort)( data[7] << 8 | ( data[7] << 1 & 255 ) );
						if( RightStickYunsigned == 0xFFFE )
							RightStickYunsigned = 0xFFFF;
						short RightStickY = (short)( -RightStickYunsigned + 0x7FFF );
						if( RightStickY == -1 )
							RightStickY = 0;

						target360.SetAxisValue( Xbox360Axis.LeftThumbX, LeftStickX );
						target360.SetAxisValue( Xbox360Axis.LeftThumbY, LeftStickY );
						target360.SetAxisValue( Xbox360Axis.RightThumbX, RightStickX );
						target360.SetAxisValue( Xbox360Axis.RightThumbY, RightStickY );
						target360.SetSliderValue( Xbox360Slider.LeftTrigger, data[8] );
						target360.SetSliderValue( Xbox360Slider.RightTrigger, data[9] );
						target360.SubmitReport();
					}
				}

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
			}
			target360.Disconnect();
		}
	}
}
