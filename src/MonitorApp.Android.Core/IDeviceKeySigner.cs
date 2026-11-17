namespace MonitorApp.Android.Core;

public interface IDeviceKeySigner
{
    byte[] ExportPublicKeySpki();

    byte[] SignP1363Sha256(ReadOnlySpan<byte> payload);
}
