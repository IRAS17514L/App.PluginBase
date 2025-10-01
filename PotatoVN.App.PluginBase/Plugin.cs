using System;
using System.Threading.Tasks;
using GalgameManager.WinApp.Base.Contracts;
using GalgameManager.WinApp.Base.Contracts.PluginUi;
using GalgameManager.WinApp.Base.Models;

namespace PotatoVN.App.PluginBase
{
    public partial class Plugin : IPlugin, IParserProvider, IPluginSetting
    {
        private IPotatoVnApi _hostApi = null!;
        
        public PluginInfo Info { get; } = new()
        {
            Id = new Guid("78f4ca27-7ffb-43b2-a5a5-b5d880db096d"),
            Name = "GetChu搜刮器",
            Description = "让你可以从getchu搜刮游戏信息！\n这是第二行描述",
        };

        public Task InitializeAsync(IPotatoVnApi hostApi)
        {
            _hostApi = hostApi;
            XamlResourceLocatorFactory.packagePath = _hostApi.GetPluginPath();
            return Task.CompletedTask;
        }

        protected Guid Id => Info.Id;
    }
}
