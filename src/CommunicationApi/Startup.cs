using System;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;
using Microsoft.OpenApi.Models;
using Arcus.Security.Secrets.Core.Caching;
using Arcus.Security.Secrets.Core.Interfaces;
using Arcus.WebApi.Security.Authentication.SharedAccessKey;
using Arcus.WebApi.Correlation;
using CommunicationApi.Interfaces;
using CommunicationApi.Security;
using CommunicationApi.Services;
using CommunicationApi.Services.Blobstorage;
using CommunicationApi.Services.Tablestorage;
using Serilog.Configuration;
using IUserMatcher = CommunicationApi.Interfaces.IUserMatcher;
using IWhatsappHandlerService = CommunicationApi.Interfaces.IWhatsappHandlerService;
using StorageSettings = CommunicationApi.Models.StorageSettings;
using TwilioUserMatcher = CommunicationApi.Services.TwilioUserMatcher;

namespace CommunicationApi
{
    public class Startup
    {
        private const string ApplicationInsightsInstrumentationKeyName = "Telemetry:ApplicationInsights:InstrumentationKey";

        /// <summary>
        /// Initializes a new instance of the <see cref="Startup"/> class.
        /// </summary>
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        /// <summary>
        /// Gets the configuration of key/value application properties.
        /// </summary>
        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            var cfgBuilder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile($"appsettings.json", true, true)
                .AddJsonFile($"appsettings.dev.json", true, true)
                .AddJsonFile($"local.settings.json", true, true)
                .AddEnvironmentVariables();
            var configuration = cfgBuilder.Build();
            
            services.AddScoped<ICachedSecretProvider>(serviceProvider =>
                new CachedSecretProvider(new SharedSecretProvider()));
            services.AddControllers(options => 
            {
                options.ReturnHttpNotAcceptable = true;
                options.RespectBrowserAcceptHeader = true;

                RestrictToJsonContentType(options);
                AddEnumAsStringRepresentation(options);

                options.Filters.Add(new SharedAccessKeyAuthenticationFilter("x-api-key", "x-api-key", "whatsapp-key"));
            });

            services.AddSingleton<IWhatsappHandlerService, WhatsappHandlerService>();
            services.AddSingleton<IUserMatcher, TwilioUserMatcher>();
            services.AddSingleton<IMessagePersister, TableMessagePersister>();
            services.AddSingleton<IMediaPersister, BlobMediaPersister>();
            services.AddHealthChecks();
            
            services.AddOptions();
            services.Configure<StorageSettings>(options => configuration.GetSection("storage").Bind(options));
            
            services.AddHealthChecks();
            services.AddCorrelation();

#if DEBUG
            var openApiInformation = new OpenApiInfo
            {
                Title = "CommunicationApi",
                Version = "v1"
            };

            services.AddSwaggerGen(swaggerGenerationOptions =>
            {
                swaggerGenerationOptions.SwaggerDoc("v1", openApiInformation);
                swaggerGenerationOptions.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, "CommunicationApi.Open-Api.xml"));
            });
#endif
        }

        private static void RestrictToJsonContentType(MvcOptions options)
        {
            var allButJsonInputFormatters = options.InputFormatters.Where(formatter => !(formatter is SystemTextJsonInputFormatter));
            foreach (IInputFormatter inputFormatter in allButJsonInputFormatters)
            {
                options.InputFormatters.Remove(inputFormatter);
            }

            // Removing for text/plain, see https://docs.microsoft.com/en-us/aspnet/core/web-api/advanced/formatting?view=aspnetcore-3.0#special-case-formatters
            options.OutputFormatters.RemoveType<StringOutputFormatter>();
        }

        private static void AddEnumAsStringRepresentation(MvcOptions options)
        {
            var onlyJsonOutputFormatters = options.OutputFormatters.OfType<SystemTextJsonOutputFormatter>();
            foreach (SystemTextJsonOutputFormatter outputFormatter in onlyJsonOutputFormatters)
            {
                outputFormatter.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            }
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            app.UseMiddleware<Arcus.WebApi.Logging.ExceptionHandlingMiddleware>();
            app.UseCorrelation();
            app.UseRouting();

            app.UseSerilogRequestLogging();
            


#if DEBUG
            app.UseSwagger(swaggerOptions =>
            {
                swaggerOptions.RouteTemplate = "api/{documentName}/docs.json";
            });
            app.UseSwaggerUI(swaggerUiOptions =>
            {
                swaggerUiOptions.SwaggerEndpoint("/api/v1/docs.json", "CommunicationApi");
                swaggerUiOptions.RoutePrefix = "api/docs";
                swaggerUiOptions.DocumentTitle = "CommunicationApi";
            });
#endif
            app.UseEndpoints(endpoints => endpoints.MapControllers());

            Log.Logger = CreateLoggerConfiguration(app.ApplicationServices).CreateLogger();
        }

        private LoggerConfiguration CreateLoggerConfiguration(IServiceProvider serviceProvider)
        {
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var instrumentationKey = configuration.GetValue<string>(ApplicationInsightsInstrumentationKeyName);
            
            return new LoggerConfiguration()
                .MinimumLevel.Debug()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
                .Enrich.FromLogContext()
                .Enrich.WithVersion()
                .Enrich.WithComponentName("API")
                .Enrich.WithCorrelationInfo()
                .WriteTo.Console()
                .WriteTo.AzureApplicationInsights(instrumentationKey);
        }
    }
}