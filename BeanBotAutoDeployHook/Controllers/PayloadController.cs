using Microsoft.AspNetCore.Mvc;
using System;
using System.Diagnostics;
using System.IO;

namespace BeanBotAutoDeployHook.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class PayloadController : ControllerBase
    {
        [HttpPost]
        public string Post()
        {
            var autodeployprocess = new Process()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "./autodeploy.sh",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            Console.Write("Reached!!!");
            //autodeployprocess.Start();
            //string result = autodeployprocess.StandardOutput.ReadToEnd();
            //autodeployprocess.WaitForExit();
            return Directory.GetCurrentDirectory();
        }
    }
}