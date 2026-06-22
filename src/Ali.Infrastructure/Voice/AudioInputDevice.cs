namespace Ali.Infrastructure.Voice;

public sealed record AudioInputDevice(int DeviceNumber, string Name)
{
    public override string ToString() => Name;
}
