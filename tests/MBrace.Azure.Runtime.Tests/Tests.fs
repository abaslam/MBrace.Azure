﻿namespace Nessos.MBrace.Azure.Runtime.Tests

open System
open System.IO
open System.Threading

open NUnit.Framework
open FsUnit

open Nessos.MBrace
open Nessos.MBrace.Runtime
open Nessos.MBrace.Library
open Nessos.MBrace.Azure.Client
open Nessos.MBrace.Azure.Runtime
open Nessos.MBrace.Azure.Runtime.Resources

[<TestFixture>]
module ``Azure Runtime Tests`` =
    
    [<Literal>]
#if DEBUG
    let repeats = 5
#else
    let repeats = 1
#endif

    let mutable runtime : Runtime option = None

    [<TestFixtureSetUp>]
    let init () =
        let selectEnv name =
            (Environment.GetEnvironmentVariable(name,EnvironmentVariableTarget.User),
                Environment.GetEnvironmentVariable(name,EnvironmentVariableTarget.Machine))
            |> function | null, s | s, null | s, _ -> s

        let config = 
            { StorageConnectionString = selectEnv "AzureStorageConn"
              ServiceBusConnectionString = selectEnv "AzureServiceBusConn"
              DefaultContainer = "bootstrap"
              DefaultQueue = "bootstrap"
              DefaultLogTable = "bootstrap"
              DefaultTable = "bootstrap" }

        let print (s : string) = if s = null then "<null>" else sprintf "%s . . ." <| s.Substring(0,15)
        printfn "config.Storage : %s" <| print config.StorageConnectionString
        printfn "config.ServiceBus : %s" <| print config.ServiceBusConnectionString
        Runtime.WorkerExecutable <- __SOURCE_DIRECTORY__ + "/../../bin/MBrace.Azure.Runtime.Standalone.exe"
        printfn "WorkerExecutable : %s" Runtime.WorkerExecutable
        runtime <- Some <| Runtime.InitLocal(config, 4)
        printfn "Runtime initialized"

    [<TestFixtureTearDown>]
    let fini () =
        //runtime |> Option.iter (fun r -> r.KillAllWorkers())
        runtime <- None

    let testContainer = "tests"

    type Counter with
        member l.Incr() = l.Increment() |> Async.RunSync

    let run (workflow : Cloud<'T>) = Option.get(runtime).RunAsync(workflow) |> Async.Catch |> Async.RunSynchronously
    let runCts (workflow : DistributedCancellationTokenSource -> Cloud<'T>) = 
        async {
            let runtime = Option.get runtime
            let! dcts = DistributedCancellationTokenSource.Init(testContainer) 
            let ct = dcts.GetLocalCancellationToken()
            return! runtime.RunAsync(workflow dcts, cancellationToken = ct) |> Async.Catch
        } |> Async.RunSync

    [<Test>]
    let ``1. Parallel : empty input`` () =
        run (Cloud.Parallel [||]) |> Choice.shouldEqual [||]

    [<Test>]
    let ``1. Parallel : simple inputs`` () =
        cloud {
            let f i = cloud { return i + 1 }
            let! results = Array.init 20 f |> Cloud.Parallel
            return Array.sum results
        } |> run |> Choice.shouldEqual 210

    [<Test>]
    let ``1. Parallel : use binding`` () =
        let counter = Counter.Init(testContainer, 0) |> Async.RunSynchronously
        cloud {
            use foo = { new ICloudDisposable with member __.Dispose () = async { return counter.Incr() |> ignore } }
            let! _ = cloud { return counter.Incr() } <||> cloud { return counter.Incr() }
            return counter.Value
        } |> run |> Choice.shouldEqual 2

        counter.Value |> should equal 3

    [<Test>]
    let ``1. Parallel : exception handler`` () =
        cloud {
            try
                let! x,y = cloud { return 1 } <||> cloud { return invalidOp "failure" }
                return x + y
            with :? InvalidOperationException as e ->
                let! x,y = cloud { return 1 } <||> cloud { return 2 }
                return x + y
        } |> run |> Choice.shouldEqual 3

    [<Test>]
    let ``1. Parallel : finally`` () =
        let counter = Counter.Init(testContainer, 0) |> Async.RunSynchronously
        cloud {
            try
                let! x,y = cloud { return 1 } <||> cloud { return invalidOp "failure" }
                return x + y
            finally
                counter.Incr () |> ignore
        } |> run |> Choice.shouldFailwith<_, InvalidOperationException>

        counter.Value |> should equal 1

    [<Test>]
    let ``1. Parallel : simple nested`` () =
        cloud {
            let f i j = cloud { return i + j + 2 }
            let cluster i = Array.init 10 (f i) |> Cloud.Parallel
            let! results = Array.init 10 cluster |> Cloud.Parallel
            return Array.concat results |> Array.sum
        } |> run |> Choice.shouldEqual 1100

    [<Test>]
    let ``1. Parallel : simple exception`` () =
        cloud {
            let f i = cloud { return if i = 15 then invalidOp "failure" else i + 1 }
            let! results = Array.init 20 f |> Cloud.Parallel
            return Array.sum results
        } |> run |> Choice.shouldFailwith<_, InvalidOperationException>


    [<Test>]
    [<Repeat(repeats)>]
    let ``1. Parallel : exception contention`` () =
        let counter = Counter.Init(testContainer, 0) |> Async.RunSynchronously
        cloud {
            try
                let! _ = Array.init 20 (fun _ -> cloud { return invalidOp "failure" }) |> Cloud.Parallel
                return raise <| new AssertionException("Cloud.Parallel should not have completed succesfully.")
            with :? InvalidOperationException ->
                counter.Incr() |> ignore
                return ()
        } |> run |> Choice.shouldEqual ()

        // test that exception continuation was fired precisely once
        counter.Value |> should equal 1


    [<Test>]
    [<Repeat(repeats)>]
    let ``1. Parallel : exception cancellation`` () =
        cloud {
            let counter = Counter.Init(testContainer, 0) |> Async.RunSynchronously
            let worker i = cloud { 
                if i = 0 then
                    invalidOp "failure"
                else
                    let _ = counter.Incr()
                    return ()
            }

            try
                let! _ = Array.init 20 worker |> Cloud.Parallel
                return raise <| new AssertionException("Cloud.Parallel should not have completed succesfully.")
            with :? InvalidOperationException ->
                return counter.Value
        } |> run |> Choice.shouldMatch(fun i -> i < 20)

    [<Test>]
    [<Repeat(repeats)>]
    let ``1. Parallel : nested exception cancellation`` () =
        cloud {
            let counter = Counter.Init(testContainer, 0) |> Async.RunSynchronously
            let worker i j = cloud {
                if i = 0 && j = 0 then
                    invalidOp "failure"
                else
                    let _ = counter.Incr()
                    return ()
            }

            try
                let cluster i = Array.init 10 (worker i) |> Cloud.Parallel |> Cloud.Ignore
                do! Array.init 10 cluster |> Cloud.Parallel |> Cloud.Ignore
                return raise <| new AssertionException("Cloud.Parallel should not have completed succesfully.")
            with :? InvalidOperationException ->
                return counter.Value
        } |> run |> Choice.shouldMatch(fun i -> i < 100)


    [<Test>]
    [<Repeat(repeats)>]
    let ``1. Parallel : simple cancellation`` () =
        let counter = Counter.Init(testContainer, 0) |> Async.RunSynchronously
        runCts(fun cts -> cloud {
            let f i = cloud {
                if i = 0 then cts.Cancel() 
                return counter.Incr() 
            }

            let! _ = Array.init 10 f |> Cloud.Parallel

            return ()
        }) |> Choice.shouldFailwith<_, OperationCanceledException>

        counter.Value |> should equal 0


    [<Test>]
    [<Repeat(repeats)>]
    let ``1. Parallel : to local`` () =
        // check local semantics are forced by using ref cells.
        cloud {
            let counter = ref 0
            let seqWorker _ = cloud {
                Interlocked.Increment counter |> ignore
            }

            let! results = Array.init 20 seqWorker |> Cloud.Parallel |> Cloud.ToLocal
            return counter.Value
        } |> run |> Choice.shouldEqual 20

    [<Test>]
    [<Repeat(repeats)>]
    let ``1. Parallel : to sequential`` () =
        // check sequential semantics are forced by deliberately
        // making use of code that is not thread-safe.
        cloud {
            let counter = ref 0
            let seqWorker _ = cloud {
                let init = counter.Value + 1
                counter := init
                return counter.Value = init
            }

            let! results = Array.init 20 seqWorker |> Cloud.Parallel |> Cloud.ToSequential
            return Array.forall id results
        } |> run |> Choice.shouldEqual true

    let wordCount size mapReduceAlgorithm : Cloud<int> =
        let mapF (text : string) = cloud { return text.Split(' ').Length }
        let reduceF i i' = cloud { return i + i' }
        let inputs = Array.init size (fun i -> "lorem ipsum dolor sit amet")
        mapReduceAlgorithm mapF 0 reduceF inputs

    let rec mapReduceRec (mapF : 'T -> Cloud<'S>) 
                    (id : 'S) (reduceF : 'S -> 'S -> Cloud<'S>)
                    (inputs : 'T []) =
        cloud {
            match inputs with
            | [||] -> return id
            | [|t|] -> return! mapF t
            | _ ->
                let left = inputs.[.. inputs.Length / 2 - 1]
                let right = inputs.[inputs.Length / 2 ..]
                let! s,s' = (mapReduceRec mapF id reduceF left) <||> (mapReduceRec mapF id reduceF right)
                return! reduceF s s'
        }

    [<Test>]
    [<Repeat(repeats)>]
    let ``1. Parallel : recursive map/reduce`` () =
        wordCount 20 mapReduceRec |> run |> Choice.shouldEqual 100

    [<Test>]
    [<Repeat(repeats)>]
    let ``1. Parallel : balanced map/reduce`` () =
        wordCount 1000 MapReduce.mapReduce |> run |> Choice.shouldEqual 5000

    [<Test>]
    let ``2. Choice : empty input`` () =
        Cloud.Choice [] |> run |> Choice.shouldEqual None

    [<Test>]
    [<Repeat(repeats)>]
    let ``2. Choice : all inputs 'None'`` () =
        cloud {
            let count = Counter.Init(testContainer, 0) |> Async.RunSynchronously
            let worker _ = cloud {
                let _ = count.Incr()
                return None
            }

            let! result = Array.init 20 worker |> Cloud.Choice
            return (count.Value, result)
        } |> run |> Choice.shouldEqual (20, None)


    [<Test>]
    [<Repeat(repeats)>]
    let ``2. Choice : one input 'Some'`` () =
        cloud {
            let count = Counter.Init(testContainer, 0) |> Async.RunSynchronously
            let worker i = cloud {
                if i = 0 then return Some i
                else
                    // check proper cancellation while we're at it.
                    let _ = count.Incr()
                    return None
            }

            let! result = Array.init 20 worker |> Cloud.Choice
            return result, count.Value
        } |> run |> Choice.shouldMatch (fun (a,b) -> a =  Some 0 && b < 20)

    [<Test>]
    [<Repeat(repeats)>]
    let ``2. Choice : all inputs 'Some'`` () =
        let successcounter = Counter.Init(testContainer, 0) |> Async.RunSynchronously
        cloud {
            let worker _ = cloud { return Some 42 }
            let! result = Array.init 20 worker |> Cloud.Choice
            let _ = successcounter.Incr()
            return result
        } |> run |> Choice.shouldEqual (Some 42)

        // ensure only one success continuation call
        successcounter.Value |> should equal 1

    [<Test>]
    [<Repeat(repeats)>]
    let ``2. Choice : simple nested`` () =
        let counter = Counter.Init(testContainer, 0) |> Async.RunSynchronously
        cloud {
            let worker i j = cloud {
                if i = 0 && j = 0 then
                    return Some(i,j)
                else
                    let _ = counter.Incr()
                    return None
            }

            let cluster i = Array.init 4 (worker i) |> Cloud.Choice
            let! result = Array.init 5 cluster |> Cloud.Choice
            return result
        } |> run |> Choice.shouldEqual (Some (0,0))

        counter.Value |> should be (lessThan 20)

    [<Test>]
    [<Repeat(repeats)>]
    let ``2. Choice : nested exception cancellation`` () =
        let counter = Counter.Init(testContainer, 0) |> Async.RunSynchronously
        cloud {
            let worker i j = cloud {
                if i = 0 && j = 0 then
                    return invalidOp "failure"
                else
                    let _ = counter.Incr()
                    return Some 42
            }

            let cluster i = Array.init 5 (worker i) |> Cloud.Choice
            return! Array.init 4 cluster |> Cloud.Choice
        } |> run |> Choice.shouldFailwith<_, InvalidOperationException>

        counter.Value |> should be (lessThan 20)

    [<Test>]
    [<Repeat(repeats)>]
    let ``2. Choice : simple cancellation`` () =
        let taskCount = Counter.Init(testContainer, 0) |> Async.RunSynchronously
        runCts(fun cts ->
            cloud {
                let worker i = cloud {
                    if i = 0 then cts.Cancel()
                    let _ = taskCount.Incr()
                    return Some 42
                }

                return! Array.init 10 worker |> Cloud.Choice
        }) |> Choice.shouldFailwith<_, OperationCanceledException>

        taskCount.Value |> should be (lessThanOrEqualTo 10)

    [<Test>]
    [<Repeat(repeats)>]
    let ``2. Choice : to local`` () =
        // check local semantics are forced by using ref cells.
        cloud {
            let counter = ref 0
            let seqWorker i = cloud {
                if i = 16 then
                    return Some i
                else
                    let _ = Interlocked.Increment counter
                    return None
            }

            let! result = Array.init 20 seqWorker |> Cloud.Choice |> Cloud.ToLocal
            return result, counter.Value
        } |> run |> Choice.shouldEqual (Some 16, 19)

    [<Test>]
    [<Repeat(repeats)>]
    let ``2. Choice : to sequential`` () =
        // check sequential semantics are forced by deliberately
        // making use of code that is not thread-safe.
        cloud {
            let counter = ref 0
            let seqWorker i = cloud {
                let init = counter.Value + 1
                counter := init
                counter.Value |> should equal init
                if i = 16 then
                    return Some ()
                else
                    return None
            }

            let! result = Array.init 20 seqWorker |> Cloud.Choice |> Cloud.ToSequential
            return result, counter.Value
        } |> run |> Choice.shouldEqual (Some(), 17)


    [<Test>]
    [<Repeat(repeats)>]
    let ``3. StartChild: task with success`` () =
        cloud {
            let count = Counter.Init(testContainer, 0) |> Async.RunSynchronously
            let task = cloud {
                return count.Incr()
            }

            let! ch = Cloud.StartChild(task)
            return! ch
        } |> run |> Choice.shouldEqual 1

    [<Test>]
    [<Repeat(repeats)>]
    let ``3. StartChild: task with exception`` () =
        cloud {
            let task = cloud {
                return invalidOp "failure"
            }

            let! ch = Cloud.StartChild(task)
            return! ch
        } |> run |> Choice.shouldFailwith<_, InvalidOperationException>

    [<Test>]
    [<Repeat(repeats)>]
    let ``3. StartChild: task with cancellation`` () =
        let count = Counter.Init(testContainer, 0) |> Async.RunSynchronously
        runCts(fun cts ->
            cloud {
                let task = cloud {
                    let _ = count.Incr()
                    return count.Incr()
                }

                let! ch = Cloud.StartChild(task)
                cts.Cancel ()
                return! ch
        }) |> Choice.shouldFailwith<_, OperationCanceledException>

        // ensure final increment was cancelled.
        count.Value |> should equal 1


    [<Test>]
    let ``4. Runtime : Get worker count`` () =
        run (Cloud.GetWorkerCount()) |> Choice.shouldEqual (runtime.Value.GetWorkers() |> Seq.length)

    [<Test>]
    let ``4. Runtime : Get current worker`` () =
        run Cloud.CurrentWorker |> Choice.shouldMatch (fun _ -> true)

    [<Test>]
    let ``4. Runtime : Get process id`` () =
        run (Cloud.GetProcessId()) |> Choice.shouldMatch (fun _ -> true)

    [<Test>]
    let ``4. Runtime : Get task id`` () =
        run (Cloud.GetTaskId()) |> Choice.shouldMatch (fun _ -> true)

//    [<Test>]
//    [<Repeat(repeats)>]
//    let ``5. Fault Tolerance : map/reduce`` () =
//        let t = runtime.Value.RunAsTask(wordCount 20 mapReduceRec)
//        do Thread.Sleep 4000
//        runtime.Value.KillAllWorkers()
//        runtime.Value.AppendWorkers 4
//        t.Result |> should equal 100