using NAudio.CoreAudioApi;
using TraductorVozTeams.Core.Devices;

namespace TraductorVozTeams.App.Audio;

public static class AudioDeviceService
{
    public static IReadOnlyList<AudioDeviceInfo> List(AudioDeviceFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        var dataFlow = flow == AudioDeviceFlow.Capture ? DataFlow.Capture : DataFlow.Render;
        var result = new List<AudioDeviceInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(dataFlow, DeviceState.Active))
        {
            using (device)
            {
                result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, flow));
            }
        }

        return result.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static MMDevice Open(string id)
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDevice(id);
    }
}
