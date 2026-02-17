namespace AnalyzePj
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
        }

        private async void btAnalyze_Click(object sender, EventArgs e)
        {
            var targetSolutionPath = tbTargetSolutionPath.Text;

            if (string.IsNullOrEmpty(targetSolutionPath))
            {
                MessageBox.Show("ソリューションファイルを指定してください。", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                tbTargetSolutionPath.Focus();
                return;
            }

            if(!File.Exists(targetSolutionPath))
            {
                MessageBox.Show("指定されたソリューションファイルが存在しません。", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                tbTargetSolutionPath.Focus();
                return;
            }

            var isShowActionParamType = cbShowActionParamType.Checked;
            var isShowActionReturnType = cbShowActionReturnType.Checked;

            tbResult.Clear();

            var progress = new Progress<string>(msg =>
            {
                tbResult.AppendText(msg + Environment.NewLine);
            });

            try
            {
                using var analyzer = new Analyzer(progress);
                var solution = await analyzer.LoadSolutionAsync(targetSolutionPath);

                tbResult.AppendText("OK: solution loaded." + Environment.NewLine + Environment.NewLine);

                var actionAnalyzer = new AnalyzeActionController(solution, progress);
                var hits = await actionAnalyzer.FindEnumRequestParam();

                foreach (var h in hits)
                {
                    tbResult.AppendText(
                        $"{h.ProjectName}\r\n" +
                        $"\t{h.ProjectFilePath}\r\n");

                    foreach (var c in h.Controllers)
                    {
                        tbResult.AppendText(
                            $"\t\t{c.ControllerClassName}\r\n" + 
                            $"\t\t {c.ControllerSourceFilePath}\r\n");

                        foreach(var a in c.Actions)
                        {
                            tbResult.AppendText($"\t\t\t{a.MethodName}\r\n");

                            if(isShowActionReturnType)
                            {
                                tbResult.AppendText($"\t\t\t {a.ReturnType}\r\n");
                            }

                            if (isShowActionParamType)
                            {
                                tbResult.AppendText($"\t\t\t {a.ParameterTypes}\r\n");
                                foreach (var p in a.EnumParams)
                                {
                                    tbResult.AppendText(
                                        $"\t\t\t\t{p.Name}\r\n" +
                                        $"\t\t\t\t {p.EnumType}\r\n");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                tbResult.AppendText(ex.ToString());
            }

        }
    }
}
