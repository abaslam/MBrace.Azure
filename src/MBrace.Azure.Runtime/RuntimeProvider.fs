﻿namespace MBrace.Azure.Runtime

#nowarn "444"

open MBrace
open MBrace.Runtime

open MBrace.Azure.Runtime
open MBrace.Azure.Runtime.Common
open System
open MBrace.Runtime.InMemory
open MBrace.Azure.Runtime.Resources
        
/// Scheduling implementation provider
type RuntimeProvider private (state : RuntimeState, wmon : WorkerManager, faultPolicy, jobId, psInfo, dependencies, isForcedLocalParallelism : bool) =

    let mkNestedCts (ct : ICloudCancellationToken) =
        let parentCts = ct :?> DistributedCancellationTokenSource
        let dcts = state.ResourceFactory.RequestCancellationTokenSource(psInfo.DefaultDirectory, parent = parentCts, elevate = false)
                   |> Async.RunSynchronously
        dcts :> ICloudCancellationTokenSource

    /// Creates a runtime provider instance for a provided job
    static member FromJob state  wmon  dependencies (job : Job) =
        new RuntimeProvider(state, wmon, job.FaultPolicy, job.JobId, job.ProcessInfo, dependencies, false)

    interface ICloudRuntimeProvider with
        member __.CreateLinkedCancellationTokenSource(parents: ICloudCancellationToken []): Async<ICloudCancellationTokenSource> = 
            async {
                match parents with
                | [||] -> 
                    let! cts = state.ResourceFactory.RequestCancellationTokenSource(psInfo.DefaultDirectory, elevate = false) 
                    return cts :> ICloudCancellationTokenSource
                | [| ct |] -> return mkNestedCts ct
                | _ -> return raise <| new System.NotSupportedException("Linking multiple cancellation tokens not supported in this runtime.")
            }
        member __.ProcessId = psInfo.Id

        member __.JobId = jobId

        member __.FaultPolicy = faultPolicy
        member __.WithFaultPolicy newPolicy = 
            new RuntimeProvider(state, wmon, newPolicy, jobId, psInfo, dependencies, isForcedLocalParallelism) :> ICloudRuntimeProvider

        member __.IsForcedLocalParallelismEnabled = isForcedLocalParallelism
        member __.WithForcedLocalParallelismSetting setting =
            new RuntimeProvider(state, wmon, faultPolicy, jobId, psInfo, dependencies, setting) :> ICloudRuntimeProvider

        member __.IsTargetedWorkerSupported = true

        member __.ScheduleLocalParallel computations = ThreadPool.Parallel(mkNestedCts, computations)
        member __.ScheduleLocalChoice computations = ThreadPool.Choice(mkNestedCts, computations)

        member __.ScheduleParallel computations = cloud {
            if isForcedLocalParallelism then
                return! ThreadPool.Parallel(mkNestedCts, computations |> Seq.map fst)
            else
                return! Combinators.Parallel state psInfo jobId dependencies faultPolicy computations
        }

        member __.ScheduleChoice computations = cloud {
            if isForcedLocalParallelism then
                return! ThreadPool.Choice(mkNestedCts, (computations |> Seq.map fst))
            else
                return! Combinators.Choice state psInfo jobId dependencies faultPolicy computations
        }

        member __.ScheduleStartAsTask(workflow : Workflow<'T>, faultPolicy, cancellationToken, ?target:IWorkerRef) =
           Combinators.StartAsCloudTask state psInfo jobId dependencies cancellationToken faultPolicy workflow target

        member __.GetAvailableWorkers () = async { 
            let! ws = wmon.GetWorkerRefs(showInactive = false)
            return ws |> Seq.map (fun w -> w :> IWorkerRef)
                      |> Seq.toArray 
            }
        member __.CurrentWorker = wmon.Current.AsWorkerRef() :> IWorkerRef
        member __.Logger = state.ResourceFactory.RequestProcessLogger(psInfo.Id) 