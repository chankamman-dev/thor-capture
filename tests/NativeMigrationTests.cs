using System;
using System.Collections.Generic;

namespace ThorCapture
{
    internal static class NativeMigrationTests
    {
        private const string Library="22222222-2222-4222-8222-222222222222", Id="33333333-3333-4333-8333-333333333333", Instance="11111111-1111-4111-8111-111111111111";
        private static Dictionary<string,object> Plan()
        {
            return (Dictionary<string,object>)NativeEditorJson.Parse(NativeEditorJson.Encode(NativeEditorClient.Map(
                "contract","cocoon.managed-migration/1","planId",Id,"libraryId",Library,"currentVersion",1,"targetVersion",2,
                "pending",new object[]{NativeEditorClient.Map("version",2,"name","domain-v2")},"versionSourcesAgree",false,
                "report",NativeEditorClient.Map("counts",NativeEditorClient.Map("library_items",1),"reports",new object[]{
                    NativeEditorClient.Map("version",2,"name","domain-v2","status","warning","counts",NativeEditorClient.Map("libraryItems",1),"warnings",NativeEditorClient.Map("catalogReconciliationRequired",1))}),
                "expiresAt",1,"sourceFingerprint",new string('a',64),"requiresConfirmation",true,"noChanges",false,"nativeDatabaseModified",false,"deviceWritesEnabled",false)));
        }
        private static Dictionary<string,object> Record()
        {return (Dictionary<string,object>)NativeEditorJson.Parse(NativeEditorJson.Encode(NativeEditorClient.Map("contract","cocoon.managed-migration/1","migrationId",Id,"libraryId",Library,"state","committed","currentVersion",2,"canRestore",true,"nativeDatabaseModified",false,"deviceWritesEnabled",false)));}
        private static void Reject(bool plan,Action<Dictionary<string,object>> mutate,string name)
        {
            Dictionary<string,object> value=plan?Plan():Record();mutate(value);bool rejected=false;
            try{if(plan)NativeEditorClient.ParseMigrationPlan(value,Library,Instance);else NativeEditorClient.ParseMigrationRecord(value,Library,Instance,Id);}
            catch(NativeEditorException error){rejected=error.Code=="EDITOR_RESPONSE_INVALID";}
            Tests.Check(rejected,"migration response rejects "+name);
        }
        public static void Run()
        {
            NativeMigrationPlan plan=NativeEditorClient.ParseMigrationPlan(Plan(),Library,Instance);
            var copy=plan.Data;copy["targetVersion"]=99d;
            Tests.Check(plan.TargetVersion==2&&Json.Number(plan.Data,"targetVersion",0)==2&&plan.InstanceId==Instance,"migration plan is a validated immutable owner-bound review snapshot");
            Tests.Check(NativeEditorClient.ParseMigrationRecord(Record(),Library,Instance,Id).CanRestore,"migration record validates committed restore availability");
            Reject(true,v=>v["libraryId"]=Instance,"foreign plan library");
            Reject(true,v=>v["deviceWritesEnabled"]=true,"plan granting device writes");
            Reject(true,v=>v["nativeDatabaseModified"]=true,"plan modifying native catalog");
            Reject(true,v=>v["requiresConfirmation"]=false,"implicit migration approval");
            Reject(true,v=>v["targetVersion"]=0d,"schema downgrade");
            Reject(true,v=>v["targetVersion"]=3d,"pending steps not reaching target version");
            Reject(true,v=>v["currentVersion"]=1.5d,"fractional schema version");
            Reject(true,v=>v["noChanges"]=true,"no-op contradicting pending steps");
            Reject(true,v=>v["sourceFingerprint"]="invalid","invalid source fingerprint");
            Reject(true,v=>v["extra"]="private","unreviewed plan fields");
            Reject(true,v=>((Dictionary<string,object>)((Dictionary<string,object>)v["report"])["counts"])["library_items"]=-1d,"negative migration counts");
            Reject(true,v=>((Dictionary<string,object>)((List<object>)((Dictionary<string,object>)v["report"])["reports"])[0])["warnings"]=NativeEditorClient.Map("details","private path"),"raw warning details");
            Reject(false,v=>v["migrationId"]=Instance,"different requested migration");
            Reject(false,v=>v["libraryId"]=Instance,"foreign record library");
            Reject(false,v=>v["state"]="unknown","unknown transaction state");
            Reject(false,v=>v["state"]="restored","restored record claiming restore availability");
            Reject(false,v=>v.Remove("nativeDatabaseModified"),"missing native mutation flag");
            Reject(false,v=>v["deviceWritesEnabled"]=true,"record granting device writes");
        }
    }
}
