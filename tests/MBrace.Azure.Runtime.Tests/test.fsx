﻿#I "../../bin/"
#r "MBrace.Core.dll"
#r "MBrace.Library.dll"
#r "FsPickler.dll"
#r "Vagrant.dll"
#r "Microsoft.WindowsAzure.Storage.dll"
#r "Microsoft.ServiceBus.dll"
#r "MBrace.Azure.Runtime.Common.dll"
#r "MBrace.Azure.Runtime.dll"
#r "MBrace.Azure.Client.dll"
#time "on"

open Nessos.MBrace
open Nessos.MBrace.Runtime
open Nessos.MBrace.Azure.Runtime
open Nessos.MBrace.Azure.Client
open System
open System.Threading

let selectEnv name =
    (Environment.GetEnvironmentVariable(name,EnvironmentVariableTarget.User),
        Environment.GetEnvironmentVariable(name,EnvironmentVariableTarget.Machine))
    |> function | null, s | s, null | s, _ -> s

let config = 
    { Configuration.Default with
        StorageConnectionString = selectEnv "AzureStorageConn"
        ServiceBusConnectionString = selectEnv "AzureServiceBusConn"  }

// local only---
#r "MBrace.Azure.Runtime.Standalone"
open Nessos.MBrace.Azure.Runtime.Standalone
Runtime.WorkerExecutable <- __SOURCE_DIRECTORY__ + "/../../bin/MBrace.Azure.Runtime.Standalone.exe"
Runtime.Spawn(config, 4)
//--------------

let runtime = Runtime.GetHandle(config)
runtime.ShowProcesses()
runtime.ShowWorkers()
runtime.ShowLogs()

let rec loop i = cloud {
    printfn "%d" i
    do! Cloud.Sleep 1000
    if i > 100 then return ()
    else return! loop(i + 2)
}

runtime.Run(Cloud.Parallel [| loop 0; loop 1|])

let ps = runtime.CreateProcess(cloud { return 42 }, name = "foo")
ps.AwaitResult()

let p = runtime.GetProcess(ps.Id)
p

runtime.Run <| Cloud.Log "FOOO"

runtime.Run(cloud { return failwith<int> "foo" })
runtime.Run <| Cloud.GetWorkerCount()
runtime.Run <| Cloud.CurrentWorker


let f i = Cloud.Parallel <| List.init i (fun x -> cloud { return x+1 })

let x = runtime.Run(f 10)


runtime.Run(Cloud.Choice <| List.init 100 (fun i -> cloud { return if i = 82 then Some 42 else None } ))

let cts = new CancellationTokenSource()
let t  = runtime.RunAsTask(cloud { while true do do! Cloud.Sleep 1000 }, cts.Token)
t.IsCompleted
t.Result
cts.Cancel()

let wordCount size mapReduceAlgorithm : Cloud<int> =
    let mapF (text : string) = cloud { return text.Split(' ').Length }
    let reduceF i i' = cloud { return i + i' }
    let inputs = Array.init size (fun i -> "lorem ipsum dolor sit amet")
    mapReduceAlgorithm mapF 0 reduceF inputs
wordCount 1000 Library.MapReduce.mapReduce 
|> runtime.Run


type Foo = { Value : int }

let ps = runtime.CreateProcess(cloud { return { Value = 42 } })
ps.AwaitResult()

runtime.ShowProcesses()
let p = runtime.GetProcess("6043e2f52ffe4c888636b3efdc6c7f3f")
p.AwaitResultBoxed()

open Nessos.MBrace.Azure.Runtime.Common
open Nessos.MBrace.Azure.Runtime.Resources

let (!) (task : Async<'T>) = Async.RunSynchronously task

Configuration.Activate(config)
ClientProvider.TableClient.GetTableReference(config.DefaultTableOrContainer).DeleteIfExists()
ClientProvider.TableClient.GetTableReference(config.DefaultLogTable).DeleteIfExists()
ClientProvider.BlobClient.GetContainerReference(config.DefaultTableOrContainer).DeleteIfExists()
ClientProvider.NamespaceClient.DeleteQueue(config.DefaultQueue)

let del x =
    ClientProvider.TableClient.ListTables(x)
    |> Seq.map (fun t -> t.DeleteAsync() |> Async.AwaitIAsyncResult)
    |> Async.Parallel
    |> Async.RunSynchronously
del "process"

ClientProvider.BlobClient.ListContainers("process")
|> Seq.map (fun t -> t.DeleteAsync() |> Async.AwaitIAsyncResult)
|> Async.Parallel
|> Async.RunSynchronously

//-------------------------------------------------------------------

let c = !Counter.Init("tmp", 1)
!c.Increment()

let l = !Latch.Init("tmp", 11)
!l.Decrement()

[|1..5|]
|> Array.map (fun _ -> async { do! l.Decrement() |> Async.Ignore })
|> Async.Parallel
|> Async.Ignore
|> Async.RunSynchronously

l.Value

//-------------------------------------------------------------------

let c = !BlobCell.Init("tmp", fun () -> 42)
!c.GetValue()

//-------------------------------------------------------------------

let q : Queue<int> = !Queue.Init("tmp")
q.Enqueue(42)
q.EnqueueBatch([|0..10|])
q.ReceiveBatch(10)
!q.TryDequeue()
q.Length

//-------------------------------------------------------------------

let rs : ResultCell<int> = !ResultCell.Init("tmp")

async { do! Async.Sleep 10000 
        do! rs.SetResult(42) }
|> Async.Start

!rs.TryGetResult()

!rs.AwaitResult()

let ra : ResultAggregator<int> = !ResultAggregator.Init("tmp", 10)
for x in 0..9 do
    printfn "%b" <| !ra.SetResult(x, x * 10)
ra.Complete

let x = !ra.ToArray()

//-------------------------------------------------------------------
type DCTS = DistributedCancellationTokenSource

let dcts0 = !DCTS.Init("tmp")
let ct0 = dcts0.GetLocalCancellationToken()

let t1 = async { while true do 
                    do! Async.Sleep 2000
                    printfn "t1" }

Async.Start(t1, ct0)
dcts0.Cancel()

let root = !DCTS.Init("tmp")
let chain = Seq.fold (fun dcts _ -> let d = !DCTS.Init("tmp", dcts) in ignore(d.GetLocalCancellationToken()) ; d ) root {1..10}

Async.Start(t1, chain.GetLocalCancellationToken())
root.Cancel()
chain.IsCancellationRequested


//--------------------------------------------------------------------
let exp = AssemblyExporter.Init("tmp")
type Foo = Foo
let xs = exp.ComputeDependencies Foo
!exp.UploadDependencies(xs)
!exp.LoadDependencies(xs)

//---------------------------------------------------------------------

let rf = ResourceFactory.Init(config)
let pmon = rf.ProcessMonitor
!pmon.CreateRecord("foo", "bar", "")
!pmon.GetProcesses()
!pmon.GetProcess("foo")
!pmon.SetCompleted("foo", "")

