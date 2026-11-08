using System;
using System.Collections.Generic;

namespace ThorCapture
{
    internal static class NativeScrapeJobTests
    {
        private const string Library = "22222222-2222-4222-8222-222222222222";
        private const string Job = "33333333-3333-4333-8333-333333333333";
        private const string Instance = "11111111-1111-4111-8111-111111111111";
        private static Dictionary<string,object> Value()
        {
            return (Dictionary<string,object>)NativeEditorJson.Parse(
                "{\"contract\":\"cocoon.scrape-jobs/1\",\"jobId\":\"" + Job + "\",\"libraryId\":\"" + Library +
                "\",\"state\":\"completed\",\"summary\":{\"total\":1,\"completed\":1,\"identified\":0,\"highConfidence\":0,\"review\":0,\"noResult\":1,\"failed\":0}," +
                "\"currentGameId\":null,\"createdAt\":1,\"finishedAt\":2,\"code\":null,\"games\":[{\"id\":1,\"candidateCount\":0,\"highConfidence\":0,\"review\":0,\"errors\":[]}]," +
                "\"reviewRequired\":true,\"deviceWritesEnabled\":false}");
        }
        private static void Reject(Action<Dictionary<string,object>> mutate,string name)
        {
            Dictionary<string,object> value=Value();mutate(value);bool rejected=false;
            try{NativeEditorClient.ParseScrapeJob(value,Library,Job,Instance);}catch(NativeEditorException e){rejected=e.Code=="EDITOR_RESPONSE_INVALID";}
            Tests.Check(rejected,"scrape progress rejects "+name);
        }
        public static void Run()
        {
            NativeScrapeJob job=NativeEditorClient.ParseScrapeJob(Value(),Library,Job,Instance);
            Tests.Check(job.Finished&&job.Completed==1&&job.InstanceId==Instance,"scrape progress validates a completed review result with owner binding");
            Reject(v=>v["libraryId"]=Instance,"foreign library");
            Reject(v=>v["jobId"]=Instance,"different requested job");
            Reject(v=>v["deviceWritesEnabled"]=true,"unexpected device writing capability");
            Reject(v=>v["reviewRequired"]=false,"automatic application");
            Reject(v=>v["finishedAt"]=null,"missing terminal timestamp");
            Reject(v=>v["currentGameId"]=1d,"active game after termination");
            Reject(v=>((Dictionary<string,object>)v["summary"])["completed"]=2d,"completed count outside batch");
            Reject(v=>((Dictionary<string,object>)v["summary"])["identified"]=1d,"summary conflicting with actual row results");
            Reject(v=>((List<object>)v["games"]).Clear(),"row count conflicting with progress");
            Reject(v=>v["code"]="SECRET_URL_VALUE","unknown terminal failure code");
            Reject(v=>v["extra"]="unreviewed","unknown envelope fields");
            Reject(v=>((Dictionary<string,object>)((List<object>)v["games"])[0])["candidateCount"]=-1d,"negative candidate count");
        }
    }
}
