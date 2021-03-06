﻿using System;
using System.Collections.Generic;
using System.Collections;
using System.Diagnostics;
using System.Text;
using System.Xml;
using System.Threading;
using System.Threading.Tasks;

using Alphaleonis.Win32.Filesystem;
using Synapse.Core;

using System.Security.Cryptography.Utility;

using config = Synapse.Handlers.Legacy.ConfigFile.Properties.Settings;

namespace Synapse.Handlers.Legacy.ConfigFile
{
	public class Workflow
	{
		protected WorkflowParameters _wfp = null;
        protected HandlerStartInfo _startInfo = null;

        public Action<string, string, LogLevel, Exception> OnLogMessage;
        public Func<string, string, StatusType, long, int, bool, Exception, bool> OnProgress;

        public Workflow(WorkflowParameters wfp)
		{
			_wfp = wfp;
		}


		public WorkflowParameters Parameters { get { return _wfp; } set { _wfp = value as WorkflowParameters; } }

		public void ExecuteAction(HandlerStartInfo startInfo)
		{
			string context = "ExecuteAction";

            _startInfo = startInfo;

			string msg = Utils.GetHeaderMessage( string.Format( "Entering Main Workflow."));
			if( OnStepStarting( context, msg ) )
			{
				return;
			}

            OnStepProgress(context, Utils.CompressXml(startInfo.Parameters));

            Stopwatch clock = new Stopwatch();
            clock.Start();

            Exception ex = null;
            try
            {
                bool isValid = ValidateParameters();

                if (isValid)
                {
                    RunMainWorkflow(startInfo.IsDryRun);
                }
                else
                {
                    OnStepProgress(context, "Package Validation Failed");
                    throw new Exception("Package Validation Failed");
                }
            }
            catch (Exception exception)
            {
                ex = exception;
            }

            bool ok = ex == null;
            msg = Utils.GetHeaderMessage(string.Format("End Main Workflow: {0}, Total Execution Time: {1}",
                ok ? "Complete." : "One or more steps failed.", clock.ElapsedSeconds()));
            OnProgress(context, msg, ok ? StatusType.Complete : StatusType.Failed, _startInfo.InstanceId, int.MaxValue, false, ex);

        }

        public virtual void RunMainWorkflow(bool isDryRun)
        {
            try
            {
                OnStepProgress("RunMainWorkflow", "Starting Main Workflow");

                _wfp.Parse();
                if (_wfp._runSequential == true || _wfp.Files.Count <= 1)
                {
                    OnStepProgress("RunMainWorkflow", "Processing Files Sequentially.");
                    foreach (FileType file in _wfp.Files)
                        MungeFile(file, isDryRun);
                }
                else
                {
                    OnStepProgress("RunMainWorkflow", "Processing Files In Parallel.");
                    Parallel.ForEach(_wfp.Files, file => MungeFile(file, isDryRun));
                }
            } catch (Exception e)
            {
                OnStepProgress("ERROR", e.Message);
                OnStepFinished("ERROR", e.StackTrace);
                throw e;
            }

        }

        public void MungeFile(FileType file, bool isDryRun)
        {
            // Parse Boolean Values
            file.Parse();
            if (file.SettingsFile != null)
                file.SettingsFile.Parse();
            foreach (SettingType setting in file.Settings)
            {
                if (setting != null)
                {
                    setting.Parse();
                    if (setting.Value != null)
                        setting.Value.Parse();
                }
            }
            if (file._CopySource == true) {
                OnStepProgress("MungeFile", "Backing Up Source File To [" + file.Source + ".orig]");
                if (isDryRun)
                    OnStepProgress("MungeFile", "IsDryRun flag is set.  Source file backup has been skipped.");
                else
                    File.Copy(file.Source, file.Source + ".orig", true);
            }

            switch (file.Type)
            {
                case ConfigType.XmlTransform:
                    OnStepProgress("MungeFile", "Starting XmlTransform From [" + file.Source + "] To [" + file.Destination + "]");
                    Munger.XMLTransform(file.Source, file.Destination, file.SettingsFile.Value, isDryRun);
                    if (isDryRun)
                        OnStepProgress("MungeFile", "IsDryRun flag is set.  File will not be saved.");
                    OnStepProgress("MungeFile", "Finished XmlTransform From [" + file.Source + "] To [" + file.Destination + "]");
                    break;
                case ConfigType.KeyValue:
                    OnStepProgress("MungeFile", "Starting KeyValue Replacement From [" + file.Source + "] To [" + file.Destination + "]");
                    Munger.KeyValue(PropertyFile.Type.Java, file.Source, file.Destination, file.SettingsFile, file.Settings, isDryRun);
                    if (isDryRun)
                        OnStepProgress("MungeFile", "IsDryRun flag is set.  File will not be saved.");
                    OnStepProgress("MungeFile", "Finished KeyValue Replacement From [" + file.Source + "] To [" + file.Destination + "]");
                    break;
                case ConfigType.INI:
                    OnStepProgress("MungeFile", "Starting INI File Replacement From [" + file.Source + "] To [" + file.Destination + "]");
                    Munger.KeyValue(PropertyFile.Type.Ini, file.Source, file.Destination, file.SettingsFile, file.Settings, isDryRun);
                    if (isDryRun)
                        OnStepProgress("MungeFile", "IsDryRun flag is set.  File will not be saved.");
                    OnStepProgress("MungeFile", "Finished INI File Replacement From [" + file.Source + "] To [" + file.Destination + "]");
                    break;
                case ConfigType.XPath:
                    OnStepProgress("MungeFile", "Starting XPath Replacement From [" + file.Source + "] To [" + file.Destination + "]");
                    Munger.XPath(file.Source, file.Destination, file.SettingsFile, file.Settings, isDryRun);
                    if (isDryRun)
                        OnStepProgress("MungeFile", "IsDryRun flag is set.  File will not be saved.");
                    OnStepProgress("MungeFile", "Finished XPath Replacement From [" + file.Source + "] To [" + file.Destination + "]");
                    break;
                case ConfigType.Regex:
                    OnStepProgress("MungeFile", "Starting Regex Replacement From [" + file.Source + "] To [" + file.Destination + "]");
                    Munger.RegexMatch(file.Source, file.Destination, file.SettingsFile, file.Settings, isDryRun);
                    if (isDryRun)
                        OnStepProgress("MungeFile", "IsDryRun flag is set.  File will not be saved.");
                    OnStepProgress("MungeFile", "Finished Regex Replacement From [" + file.Source + "] To [" + file.Destination + "]");
                    break;
                default:
                    OnStepProgress("RunMainWorkflow", "Unsupported ConfigFile Type [" + file.Type.ToString() + "].");
                    break;
            }
        }

        bool ValidateParameters()
        {
            string context = "Validate";
            const int padding = 50;

            OnStepProgress(context, Utils.GetHeaderMessage("Begin [PrepareAndValidate]"));

            String[] errors = _wfp.PrepareAndValidate();

            if (_wfp.IsValid == false)
                foreach (String error in errors)
                    OnStepProgress(context, error);

            OnStepProgress(context, Utils.GetMessagePadRight("IsValidSourceFiles", _wfp.IsValidSourceFiles, padding));
            OnStepProgress(context, Utils.GetMessagePadRight("IsValidDestinations", _wfp.IsValidDestinations, padding));
            OnStepProgress(context, Utils.GetMessagePadRight("IsValidSettingsFiles", _wfp.IsValidSettingsFiles, padding));
            OnStepProgress(context, Utils.GetMessagePadRight("HasSettings", _wfp.HasSettings, padding));
            OnStepProgress(context, Utils.GetMessagePadRight("IsValid", _wfp.IsValid, padding));
            OnStepProgress(context, Utils.GetHeaderMessage("End [PrepareAndValidate]"));

            return _wfp.IsValid;
        }


        #region NotifyProgress Events
		int _cheapSequence = 0;

		/// <summary>
		/// Notify of step beginning. If return value is True, then cancel operation.
		/// Defaults: PackageStatus.Running, Id = _cheapSequence++, Severity = 0, Exception = null.
		/// </summary>
		/// <param name="context">The method name.</param>
		/// <param name="message">Descriptive message.</param>
		/// <returns>AdapterProgressCancelEventArgs.Cancel value.</returns>
		bool OnStepStarting(string context, string message)
		{
            OnProgress(context, message, StatusType.Running, _startInfo.InstanceId, _cheapSequence++, false, null);
            return false;
        }

        /// <summary>
        /// Notify of step progress.
        /// Defaults: PackageStatus.Running, Id = _cheapSequence++, Severity = 0, Exception = null.
        /// </summary>
        /// <param name="context">The method name.</param>
        /// <param name="message">Descriptive message.</param>
        protected void OnStepProgress(string context, string message)
		{
            OnProgress(context, message, StatusType.Running, _startInfo.InstanceId, _cheapSequence++, false, null);
		}

		/// <summary>
		/// Notify of step completion.
		/// Defaults: PackageStatus.Running, Id = _cheapSequence++, Severity = 0, Exception = null.
		/// </summary>
		/// <param name="context">The method name or workflow activty.</param>
		/// <param name="message">Descriptive message.</param>
		protected void OnStepFinished(string context, string message)
		{
            OnProgress(context, message, StatusType.Running, _startInfo.InstanceId, _cheapSequence++, false, null);
		}
		#endregion

	}

}