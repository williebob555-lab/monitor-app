using MonitorApp.Android.Core;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.Mobile;

namespace MonitorApp.Android;

/// <summary>Peer identity whose P-256 private key never leaves Android Keystore.</summary>
public sealed class KeystorePeerIdentity : ILocalPeerIdentityProvider, IDisposable
{
    private readonly KeystoreKey key;

    public KeystorePeerIdentity(string alias)
    {
        var signer = new AndroidKeystoreDeviceKeySigner(alias);
        key = new KeystoreKey(signer);
        PublicIdentity = MobilePeerIdentity.CreatePublicIdentity(signer.ExportPublicKeySpki());
    }

    public PeerPublicIdentity PublicIdentity { get; }

    public IPeerIdentityKey IdentityKey => key;

    public void Dispose() => key.Dispose();

    private sealed class KeystoreKey(IDeviceKeySigner signer) : IPeerIdentityKey
    {
        public byte[] ExportSubjectPublicKeyInfo() => signer.ExportPublicKeySpki();

        public byte[] Sign(ReadOnlySpan<byte> payload) => signer.SignP1363Sha256(payload);

        public void Dispose()
        {
        }
    }
}
