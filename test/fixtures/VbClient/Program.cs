using RemoteFacadeHost.Client;
using VbLib;

await using var vbHost = RemoteHost.At("http://vb:8080");
var store = await vbHost.GetAsync<IVbStore>();

Console.WriteLine("RESULT: vb-sync " + store.Describe());

store.Touch("vb.txt");
Console.WriteLine("RESULT: vb-async " + await store.ReadAsync("vb.txt"));
