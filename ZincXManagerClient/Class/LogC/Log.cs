using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ZincXManagerClient.Class.LogC
{
    public class Log
    {
        private FileStream logS = new FileStream("log.log", FileMode.Append, FileAccess.Write, FileShare.Write);//创建文件流LogS
        string logLevelText;
        string time = DateTime.Now.ToString("hh:mm:ss");


        private void writeLog(char logLevel, string text)//写入日志
        {
            
            using (StreamWriter logW = new StreamWriter(logS))
            {
                switch (logLevel) 
                {
                    case 'i':
                            logLevelText = "[INFO]";break;
                    case 'w':
                            logLevelText = "[INFO]"; break;
                    case 'd':
                            logLevelText = "[INFO]"; break;
                    case 'e':
                            logLevelText = "[INFO]";break;
                    case 'f':
                            logLevelText = "[INFO]"; break;
                    
                }
                    
                logW.WriteLine("{0}[{1}]{2}",logLevelText,time,text);
            }
        }
    }
}
