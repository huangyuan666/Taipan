﻿namespace ES.Taipan.Fingerprinter

open System
open System.Linq
open System.Threading
open System.Collections.Concurrent
open System.Collections.Generic
open ES.Taipan.Infrastructure.Network
open ES.Taipan.Infrastructure.Messaging
open ES.Taipan.Infrastructure.Threading
open ES.Taipan.Infrastructure.Service
open ES.Fslog

type DefaultWebAppFingerprinterMetrics(requestsToProcess: _ seq) =
    inherit ServiceMetrics("Web Application Fingerprinter")
    let mutable _numRequestProcessed = 0
    
    member this.RequestProcessed() =
        _numRequestProcessed <- _numRequestProcessed + 1
        this.PercentageCompletation()

    member this.PercentageCompletation() =
        let percentage = float _numRequestProcessed / float (requestsToProcess.Count())
        this.AddMetric("Completation", percentage.ToString())

type DefaultWebAppFingerprinter(settings: WebAppFingerprinterSettings, webApplicationFingerprintRepository: IWebApplicationFingerprintRepository, webServerFingerprinter: IWebServerFingerprinter, webRequestor: IWebPageRequestor, messageBroker: IMessageBroker, logProvider: ILogProvider) as this =    
    let mutable _processCompletedInvoked = false
    let mutable _stopRequested = false
    let _numOfParallelRequestWorkers = 20
    let _requestsToProcess = new BlockingCollection<FingerprintRequest>()       
    let _stateController = new ServiceStateController()
    let _taskManager = new TaskManager(_stateController, true, false, _numOfParallelRequestWorkers)
    let _processCompleted = new Event<IService>()
    let _initializationCompleted = new Event<IService>()
    let _noMoreWebRequestsToProcess = new Event<IWebAppFingerprinter>()
    let _logger = new WebAppFingerprinterLogger()    
    let _serviceDiagnostics = new ServiceDiagnostics()
    let _serviceMetrics = new DefaultWebAppFingerprinterMetrics(_requestsToProcess)
    let _statusMonitor = new Object()
    let _runToCompletationCalledLock = new ManualResetEventSlim()

    let handleControlMessage(sender: Object, message: Envelope<String>) =
        match message.Item.ToUpper() with
        | "STOP" -> this.Stop()
        | "PAUSE" -> this.Pause()
        | "RESUME" -> this.Resume()
        | _ -> ()

    let stopRequested() =
        _stateController.IsStopped || _stopRequested

    let _fingerprintWorkFlow = 
        new FingerprintWorkflow(
            settings,
            messageBroker,
            webServerFingerprinter,
            webApplicationFingerprintRepository,
            webRequestor,
            _taskManager,
            _numOfParallelRequestWorkers,
            _stateController,
            stopRequested,
            logProvider
        )

    let completeProcess() =
        if not _processCompletedInvoked then
            _processCompletedInvoked <- true
            _stateController.ReleaseStopIfNecessary()
            _stateController.UnlockPause()
            _processCompleted.Trigger(this)
            _serviceMetrics.AddMetric("Status", "Completed")

    let processFingerprintRequest(fingerprintRequest: FingerprintRequest) =
        if not _stateController.IsStopped && not _stopRequested then            
            this.Fingerprint(fingerprintRequest) |> ignore

    let triggerIdleState() =
        _serviceMetrics.AddMetric("Status", "Idle")
        _serviceDiagnostics.GoIdle()
        _logger.GoIdle()
        _noMoreWebRequestsToProcess.Trigger(this)
        
    let checkFingerprinterState() =
        if _requestsToProcess |> Seq.isEmpty then
            triggerIdleState()

    let webAppFingerprinterLoop() = Async.Start <| async {
        // main process loop
        _initializationCompleted.Trigger(this)
        _serviceMetrics.AddMetric("Last fingerprinted directory", "<no one>")
        for fingerprintRequest in _requestsToProcess.GetConsumingEnumerable() do
            if not _stateController.IsStopped && not _stopRequested then
                lock _statusMonitor (fun () ->
                    _serviceDiagnostics.Activate()
                    _serviceMetrics.AddMetric("Status", "Running")
                    processFingerprintRequest(fingerprintRequest)
                    _serviceMetrics.RequestProcessed()
                    checkFingerprinterState()
                )

        if _stopRequested then
            // wait until the run to completation is called
            checkFingerprinterState()
            _stateController.ReleaseStopIfNecessary()
            _logger.WaitRunToCompletation()
            _runToCompletationCalledLock.Wait()  

        // no more fingerprint requests to process
        completeProcess()
    } 
        
    let handleFingerprintRequestMessage(sender: Object, fingerprintRequest: Envelope<FingerprintRequest>) =     
        if not _stateController.IsStopped && not _stopRequested && not _requestsToProcess.IsAddingCompleted then   
            _requestsToProcess.Add(fingerprintRequest.Item)

    let handleGetAvailableVersionsMessage(sender: Object, getAvailableVersions: Envelope<GetAvailableVersionsMessage>) =  
        let message = getAvailableVersions.Item
        match webApplicationFingerprintRepository.GetAllWebApplications() |> Seq.tryFind(fun app -> app.Name.Equals(message.Application, StringComparison.OrdinalIgnoreCase)) with
        | Some webApp -> 
            message.Versions <- 
                webApp.Versions 
                |> Seq.map(fun ver -> ver.Version) 
                |> Seq.toList
        | None -> ()
        
    let isValidForFingerprint =
        let alreadyAnalyzedPath = new HashSet<String>()
        fun (fingRequest: FingerprintRequest) ->
            let pathDirectory = HttpUtility.getAbsolutePathDirectory(fingRequest.Request.Uri)
            if alreadyAnalyzedPath |> Seq.isEmpty then
                alreadyAnalyzedPath.Add(pathDirectory)
            else
                alreadyAnalyzedPath.Add(pathDirectory) && settings.BeRecursive

    do 
        logProvider.AddLogSourceToLoggers(_logger)
        webRequestor.HttpRequestor.Metrics <- _serviceMetrics.GetSubMetrics("DefaultWebAppFingerprinterHttpRequestor_" + webRequestor.HttpRequestor.Id.ToString("N"))

        // the requests done don't need Javascript engine
        webRequestor.HttpRequestor.Settings.UseJavascriptEngineForRequest <- false
        webRequestor.HttpRequestor.Settings.AllowAutoRedirect <- false

        // disable authentication since it is not needed
        webRequestor.HttpRequestor.Settings.Authentication.Enabled <- false

        // message subscription
        messageBroker.Subscribe<String>(handleControlMessage)
        messageBroker.Subscribe<FingerprintRequest>(handleFingerprintRequestMessage)
        messageBroker.Subscribe<GetAvailableVersionsMessage>(handleGetAvailableVersionsMessage)
        messageBroker.Subscribe<RequestMetricsMessage>(fun (sender, msg) -> msg.Item.AddResult(this, _serviceMetrics))

    new(settings: WebAppFingerprinterSettings, webApplicationFingerprintRepository: IWebApplicationFingerprintRepository, webServerFingerprinter: IWebServerFingerprinter, webRequestor: IWebPageRequestor, logProvider: ILogProvider) = new DefaultWebAppFingerprinter(settings, webApplicationFingerprintRepository, webServerFingerprinter, webRequestor, new NullMessageBroker(), logProvider)

    static member WebAppFingerprinterId = Guid.Parse("125D1940-8F83-4373-9BB9-9F8C17D1C796")
    member val ServiceId = DefaultWebAppFingerprinter.WebAppFingerprinterId with get
    member this.ProcessCompleted = _processCompleted.Publish
    member this.InitializationCompleted = _initializationCompleted.Publish
    member this.NoMoreWebRequestsToProcess = _noMoreWebRequestsToProcess.Publish
    member val Diagnostics = _serviceDiagnostics with get

    member this.Pause() = 
        // if there aren't requests that must be processed the not blocking call of Pause must be executed because the
        // main loop is waiting for requests and until this requirement old the WaitInInPauseState method
        // isn't called, so a deadlock may occour.
        let action =
            if Monitor.TryEnter(_statusMonitor) && _serviceDiagnostics.IsIdle then _stateController.NotBlockingPause
            else _stateController.Pause

        if action() then
            _logger.WebAppFingerprinterPaused()
            _serviceMetrics.AddMetric("Status", "Paused")
                
    member this.Resume() = 
        if _stateController.ReleasePause() then
            _logger.WebAppFingerprinterResumed()            
            _serviceMetrics.AddMetric("Status", "Running")
            checkFingerprinterState()
        
    member this.Stop() = 
        _logger.StopRequested() 
        _stopRequested <- true  
        _requestsToProcess.CompleteAdding()

        if _stateController.Stop() then
            _logger.WebAppFingerprinterStopped()     
            _serviceMetrics.AddMetric("Status", "Stopped")

    member this.RunToCompletation() =
        _serviceMetrics.AddMetric("Status", "Run to completation")
        _logger.RunToCompletation()

        // must verify that all the plugins completed their work before to invoke the completeProcess method
        _requestsToProcess.CompleteAdding()

        // unlock if locked by stop request
        _runToCompletationCalledLock.Set()

    member this.Fingerprint(fingerprintRequest: FingerprintRequest) =
        let webApplicationFound = new List<WebApplicationIdentified>()
        if isValidForFingerprint(fingerprintRequest) then
            _serviceMetrics.AddMetric("Last fingerprinted directory", fingerprintRequest.Request.Uri.ToString())
            _fingerprintWorkFlow.Fingerprint(fingerprintRequest, webApplicationFound)
        
        webApplicationFound

    member this.Activate() =
        // run the main loop
        webAppFingerprinterLoop()

    interface IDisposable with
        member this.Dispose() =
            // dispose web requestor, this is importance, since if we use the Javascript
            // Engine the dispose will tear down the browser
            match webRequestor with
            | :? IDisposable as disposable -> disposable.Dispose()
            | _ -> ()

    interface IWebAppFingerprinter with       

        member this.ServiceId
            with get() = this.ServiceId

        member this.Diagnostics
            with get() = this.Diagnostics
            
        member this.Fingerprint(fingerprintRequest: FingerprintRequest) =
            this.Fingerprint(fingerprintRequest)

        member this.Pause() = 
            this.Pause()

        member this.Resume() = 
            this.Resume()
        
        member this.Stop() =    
            this.Stop()

        member this.RunToCompletation() =
            this.RunToCompletation()

        member this.Activate() =
            this.Activate()

        member this.ProcessCompleted
            with get() = this.ProcessCompleted

        member this.InitializationCompleted
            with get() = this.InitializationCompleted

        member this.NoMoreWebRequestsToProcess
            with get() = this.NoMoreWebRequestsToProcess