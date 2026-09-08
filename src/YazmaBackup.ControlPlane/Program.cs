using YazmaBackup.ControlPlane.Endpoints;
using YazmaBackup.ControlPlane.Hosting;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.AddControlPlaneServices();
var app = builder.Build();

await app.InitializeControlPlaneAsync().ConfigureAwait(false);
app.UseControlPlanePipeline(settings);
app.MapControlPlaneEndpoints(settings);

await app.RunAsync().ConfigureAwait(false);
