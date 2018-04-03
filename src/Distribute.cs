﻿namespace SerDistribute
{
    #region Usings
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using NLog;
    using SerApi;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    #endregion

    public class Distribute
    {
        #region Logger
        private static Logger logger = LogManager.GetCurrentClassLogger();
        #endregion

        private T GetSettings<T>(JToken json, bool typeOnly = false) where T : ISettings, new()
        {
            try
            {
                if (typeOnly)
                {
                    var jProperty = json as JProperty;
                    switch (jProperty?.Name)
                    {
                        case "mail":
                            return new T() { Type = SettingsType.MAIL };
                        case "hub":
                            return new T() { Type = SettingsType.HUB };
                        case "file":
                            return new T() { Type = SettingsType.FILE };
                    }
                }

                return JsonConvert.DeserializeObject<T>(json.First().ToString());
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                return default(T);
            }
        }

        public string Run(string resultFolder, bool onDemand = false)
        {
            try
            {
                var execute = new ExecuteManager();
                logger.Info("Read json result files...");
                string[] jsonPaths = Directory.GetFiles(resultFolder, "*.json", SearchOption.TopDirectoryOnly);
                foreach (var jsonPath in jsonPaths)
                {
                    if (!File.Exists(jsonPath))
                    {
                        logger.Error($"The json result path \"{jsonPath}\" not found.");
                        continue;
                    }

                    var json = File.ReadAllText(jsonPath);
                    var result = JsonConvert.DeserializeObject<JobResult>(json,
                                 new JsonSerializerSettings() { TypeNameHandling = TypeNameHandling.Auto });

                    if (result.Status != TaskStatusInfo.SUCCESS)
                    {
                        logger.Warn($"The result \"{result.Status}\" of the report {jsonPath} is not correct. The report is ignored.");
                        continue;
                    }

                    var mailList = new List<MailSettings>();
                    var uploadTasks = new List<Task>();
                    foreach (var report in result.Reports)
                    {
                        var locations = report.Evaluate.Children();
                        foreach (var location in locations)
                        {
                            var settings = GetSettings<FileSettings>(location, true);
                            switch (settings.Type)
                            {
                                case SettingsType.FILE:
                                    //copy reports
                                    logger.Info("Check - Copy Files...");
                                    var fileSettings = GetSettings<FileSettings>(location);
                                    execute.CopyFile(fileSettings, report.Paths, report.Name);
                                    break;
                                case SettingsType.HUB:
                                    //upload to hub
                                    logger.Info("Check - Upload to hub...");
                                    var hubSettings = GetSettings<HubSettings>(location);
                                    var task = execute.UploadToHub(hubSettings, report.Paths, report.Name, onDemand);
                                    if (task != null)
                                        uploadTasks.Add(task);
                                    break;
                                case SettingsType.MAIL:
                                    //cache mail infos
                                    logger.Info("Check - Cache Mail...");
                                    var mailSettings = GetSettings<MailSettings>(location);
                                    var active = mailSettings?.Active ?? true;
                                    if (active)
                                    {
                                        mailSettings.Paths = report.Paths;
                                        mailSettings.ReportName = report.Name;
                                        mailList.Add(mailSettings);
                                    }
                                    break;
                                default:
                                    logger.Warn($"The delivery type of json {location} is unknown.");
                                    break;
                            }
                        }
                    }

                    //wait for all upload tasks
                    Task.WaitAll(uploadTasks.ToArray());

                    //Send Mail
                    logger.Info("Check - Send Mails...");
                    execute.SendMails(mailList);
                }

                if (onDemand)
                    return execute.OnDemandDownloadLink;
                else
                    return "OK";
            }
            catch (Exception ex)
            {
                logger.Error(ex);
                return null;
            }
        }
    }
}