using Microsoft.Build.Locator;

namespace AnalyzePj
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            //var builders = MSBuildLocator.QueryVisualStudioInstances();



            MSBuildLocator.RegisterMSBuildPath("C:\\Program Files\\dotnet\\sdk\\\\10.0.102");

            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();
            Application.Run(new Form1());
        }
    }
}