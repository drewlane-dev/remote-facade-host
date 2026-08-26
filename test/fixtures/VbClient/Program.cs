using RemoteFacadeHost.Client;
using VbLib;

IVbStore store = RemoteFacade.For<IVbStore>("http://vb:8080");

Console.WriteLine("RESULT: vb-sync " + store.Describe());

store.Touch("vb.txt");
Console.WriteLine("RESULT: vb-async " + await store.ReadAsync("vb.txt"));
