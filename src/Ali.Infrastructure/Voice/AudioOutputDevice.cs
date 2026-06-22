namespace Ali.Infrastructure.Voice;

public sealed record AudioOutputDevice(int DeviceNumber, string Name)
{
    public override string ToString() => Name;
}
