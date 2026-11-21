using MonitorApp.Peer.Service;

var app = await PeerHostApplication.CreateAsync(args).ConfigureAwait(false);
await app.RunAsync().ConfigureAwait(false);

public partial class Program;
