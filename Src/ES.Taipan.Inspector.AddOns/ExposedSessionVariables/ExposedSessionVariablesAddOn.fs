﻿namespace ES.Taipan.Inspector.AddOns.ExposedSessionVariables

open System
open System.Text
open System.Threading
open System.Collections.Generic
open System.Collections.Concurrent
open System.Text.RegularExpressions
open ES.Taipan.Inspector
open ES.Taipan.Inspector.AddOns
open ES.Taipan.Infrastructure.Service
open ES.Taipan.Infrastructure.Messaging
open ES.Taipan.Infrastructure.Network
open ES.Taipan.Infrastructure.Text
open ES.Taipan.Fingerprinter
open ES.Taipan.Crawler
open ES.Fslog

type ExposedSessionVariablesAddOn() as this =
    inherit BaseStatelessAddOn("Exposed Session Variables AddOn", string ExposedSessionVariablesAddOn.Id, 1)
    let _signaledLeakage = new HashSet<String>()

    let _sessionTokens = [
        // src: https://github.com/danielmiessler/SecLists/blob/master/Miscellaneous/web/session-id.txt        
        "SITESERVER"
        "SID"
        "VIEWSTATE"
        "ZENID"
        "JSESSIONID"
        "PHPSESSIONID"
        "JSESSIONID"
        "SESSIONID"
        "PHPSESSID"
        "SESSID"
        "ASPSESSIONID"
        "JWESESSIONID"
        "SESSION_IO"
        "SESSION-ID"
        "CFID"
        "CFTOKEN"
        "ASP.NET_SESSIONID"
    ]
    
    let reportSecurityIssue(testRequest: TestRequest, name: String, value: String) =
        if _signaledLeakage.Add(testRequest.WebRequest.HttpRequest.Uri.AbsolutePath) then
            let securityIssue = 
                new SecurityIssue(
                    ExposedSessionVariablesAddOn.Id, 
                    Name = "Exposed Session Variables", 
                    Uri = testRequest.WebRequest.HttpRequest.Uri, 
                    EntryPoint = EntryPoint.QueryString,
                    Note = String.Format("{0}={1}", name, value)
                )
            securityIssue.Transactions.Add(testRequest.WebRequest, testRequest.WebResponse)
            securityIssue.Details.Properties.Add("tokenName", name)
            securityIssue.Details.Properties.Add("tokenValue", value)

            this.Context.Value.AddSecurityIssue(securityIssue)

    static member Id = Guid.Parse("C76061B2-52AE-4C64-BD2E-71EA3AC41B93")
                        
    default this.Scan(testRequest: TestRequest, stateController: ServiceStateController) =
        if _signaledLeakage.Contains(testRequest.WebRequest.HttpRequest.Uri.AbsolutePath) |> not && testRequest.WebRequest.HttpRequest.Method = HttpMethods.Get then            
            let query = 
                if testRequest.WebRequest.HttpRequest.Uri.Query.StartsWith("?") 
                then testRequest.WebRequest.HttpRequest.Uri.Query.Substring(1)
                else String.Empty
            WebUtility.getParametersFromData(query)
            |> List.filter(fun (name, value) -> _sessionTokens |> List.contains(name.Trim().ToUpper()) )
            |> List.iter(fun (name, value) ->
                reportSecurityIssue(testRequest, name, value)
            )