using HidSharp;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;

namespace StadiEm.Device
{
	public class BaseHIDController
	{
		public HidDevice _device;
		public HidStream _stream;
		public int _index;
		public bool running;
		public ViGEmClient _client;
		public IXbox360Controller target360;
		public IDualShock4Controller targetDS4;

		public bool pluggedIn360 = false;

		public BaseHIDController( HidDevice device, HidStream stream, ViGEmClient client, int index )
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
		}

		public virtual void unplug( bool joinInputThread = true )
		{
		}
	}
}
