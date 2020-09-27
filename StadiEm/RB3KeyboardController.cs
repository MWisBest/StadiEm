using HidSharp;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using System;
using System.IO;
using System.Threading;

namespace StadiEm
{
	class RB3KeyboardController : BaseHIDController
	{
		public const ushort VID = 0x1BAD;
		public const ushort PID = 0x3330;

		public Thread inputThread;

		public RB3KeyboardController( HidDevice device, HidStream stream, ViGEmClient client, int index ) : base( device, stream, client, index )
		{
			target360.FeedbackReceived += this.Target360_FeedbackReceived;
			targetDS4.FeedbackReceived += this.TargetDS4_FeedbackReceived;

			inputThread = new Thread( () => input_thread() );
			inputThread.Priority = ThreadPriority.AboveNormal;
			inputThread.Name = "Controller #" + index + " Input";
			inputThread.Start();
		}

		private void Target360_FeedbackReceived( object sender, Xbox360FeedbackReceivedEventArgs e )
		{
		}

		private void TargetDS4_FeedbackReceived( object sender, DualShock4FeedbackReceivedEventArgs e )
		{
		}

		public override void unplug( bool joinInputThread = true )
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
			// Does not seem to work.
			byte[] ledReport = { 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00 };
			_stream.Write( ledReport );
			int footPedalInc = -1;
			target360.Connect();
			_stream.ReadTimeout = Timeout.Infinite;
			byte[] data = new byte[_device.GetMaxInputReportLength()];
			while( running )
			{
				int read = 0;
				try
				{
					read = _stream.Read( data );
				}
				catch( TimeoutException )
				{
					read = 0;
				}
				catch
				{
					unplug( joinInputThread: false );
				}

				if( read > 0 )
				{
					if( read == 28 && data[15] != 0x00 )
					{
						target360.ResetReport();
						;
						bool up = false;
						bool down = false;
						bool left = false;
						bool right = false;

						switch( data[3] )
						{
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


						if( ( data[2] & 0x10 ) != 0 )
							target360.SetButtonState( Xbox360Button.Guide, true );
						if( ( data[2] & 0x02 ) != 0 )
							target360.SetButtonState( Xbox360Button.Start, true );
						if( (data[14] & 0x80) != 0 || (data[2] & 0x01) != 0 ) // minus button, overdrive button
							target360.SetButtonState( Xbox360Button.Back, true );

						if( ( data[15] & 0x80 ) != 0 && (data[21] & 0x01) != 0 ) // foot pedal jack input bit, and detect bit for good measure?
						{
							// increment does on-release triggering too for us
							if( footPedalInc < 200 )
							{
								target360.SetButtonState( Xbox360Button.Back, true );
								++footPedalInc;
							}
						}
						else
						{
							if( footPedalInc >= 200 )
							{
								target360.SetButtonState( Xbox360Button.Back, true );
								footPedalInc = -1;
							}
						}

						byte effectpad = (byte)( ( data[16] & 0x7F ) << 1 );
						if( effectpad > 0 )
						{
							effectpad = (byte)( effectpad + 1 );
						}

						ushort RightStickXunsigned = (ushort)( effectpad << 8 | ( effectpad << 1 & 255 ) );
						if( RightStickXunsigned == 0xFFFE )
							RightStickXunsigned = 0xFFFF;
						short RightStickX = (short)( RightStickXunsigned - 0x8000 );
						target360.SetAxisValue( Xbox360Axis.RightThumbX, RightStickX );

						;

						byte k1 = data[6];
						byte k2 = data[7];
						byte k3 = data[8];
						byte k4 = (byte)(data[9] & 0x80);

						if( ( k2 & 0x08 ) != 0 ) // green key
							target360.SetButtonState( Xbox360Button.A, true );
						if( ( k2 & 0x02 ) != 0 ) // red fret
							target360.SetButtonState( Xbox360Button.B, true );
						if( ( k3 & 0x80 ) != 0 ) // yellow fret
							target360.SetButtonState( Xbox360Button.Y, true );
						if( ( k3 & 0x40 ) != 0 ) // blue fret
							target360.SetButtonState( Xbox360Button.X, true );
						if( ( k3 & 0x10 ) != 0 ) // orange fret
							target360.SetButtonState( Xbox360Button.LeftShoulder, true );

						target360.SubmitReport();
					}
				}
			}
			target360.Disconnect();
		}
	}
}
