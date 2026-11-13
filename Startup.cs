using System.Net.Http.Headers;

public class Startup
{
    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    public IConfiguration Configuration { get; }

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddControllers(options => options.Filters.Add<ApsExceptionFilter>());
        services.AddDataProtection();
        services.AddTransient<ApsRetryHandler>();
        services.AddHttpClient(APS.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromMinutes(10);
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            })
            .AddHttpMessageHandler<ApsRetryHandler>();

        var clientID = Configuration["APS_CLIENT_ID"];
        var clientSecret = Configuration["APS_CLIENT_SECRET"]; // required: Team Admin API supports Traditional Web Apps only
        var callbackURL = Configuration["APS_CALLBACK_URL"];
        var baseURL = Configuration["APS_BASE_URL"];
        if (string.IsNullOrEmpty(clientID) || string.IsNullOrEmpty(clientSecret) || string.IsNullOrEmpty(callbackURL))
        {
            throw new ApplicationException("Missing required environment variables APS_CLIENT_ID, APS_CLIENT_SECRET or APS_CALLBACK_URL.");
        }
        services.AddSingleton(sp => new APS(clientID, clientSecret, callbackURL, baseURL, sp.GetRequiredService<IHttpClientFactory>()));
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
        });
    }
}
