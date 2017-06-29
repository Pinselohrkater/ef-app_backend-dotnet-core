﻿using System;
using System.IO;
using System.Text;
using Amazon;
using Amazon.CloudWatchLogs;
using Amazon.Runtime;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using Eurofurence.App.Domain.Model.MongoDb;
using Eurofurence.App.Domain.Model.MongoDb.DependencyResolution;
using Eurofurence.App.Server.Services.Abstraction.Telegram;
using Eurofurence.App.Server.Services.Abstractions.PushNotifications;
using Eurofurence.App.Server.Services.Abstractions.Security;
using Eurofurence.App.Server.Services.Security;
using Eurofurence.App.Server.Web.Extensions;
using Eurofurence.App.Server.Web.Swagger;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Serilog;
using Serilog.Sinks.AwsCloudWatch;
using Swashbuckle.AspNetCore.Swagger;

namespace Eurofurence.App.Server.Web
{
    public class Startup
    {
        private readonly IHostingEnvironment _hostingEnvironment;

        public Startup(IHostingEnvironment hostingEnvironment)
        {
            _hostingEnvironment = hostingEnvironment;

            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json");

            Configuration = builder.Build();
        }

        public IConfigurationRoot Configuration { get; set; }

        public IServiceProvider ConfigureServices(IServiceCollection services)
        {
            var client = new MongoClient(Configuration["mongoDb:url"]);
            var database = client.GetDatabase(Configuration["mongoDb:database"]);

            BsonClassMapping.Register();

            services.AddCors(options =>
            {
                options.AddPolicy("CorsPolicy",
                    cpb => cpb.AllowAnyOrigin()
                        .AllowAnyMethod()
                        .AllowAnyHeader()
                        .AllowCredentials());
            });

            services.AddMvc(options => options.MaxModelValidationErrors = 0)
                .AddJsonOptions(options =>
                {
                    options.SerializerSettings.ContractResolver = new BaseFirstContractResolver();
                    options.SerializerSettings.Formatting = Formatting.Indented;
                    options.SerializerSettings.Converters.Add(new StringEnumConverter());
                    options.SerializerSettings.Converters.Add(new IsoDateTimeConverter
                    {
                        DateTimeFormat = "yyyy'-'MM'-'dd'T'HH':'mm':'ss.fffK"
                    });
                });

            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();

            services.AddSwaggerGen(options =>
            {
                options.SwaggerDoc("v2", new Info
                {
                    Version = "v2",
                    Title = "Eurofurence API for Mobile Apps",
                    Description = "",
                    TermsOfService = "None",
                    Contact = new Contact {Name = "Luchs", Url = "https://telegram.me/pinselohrkater"}
                });

                options.AddSecurityDefinition("Bearer", new ApiKeyScheme
                {
                    Description = "JWT Authorization header using the Bearer scheme. Example: \"Bearer {token}\"",
                    Name = "Authorization",
                    In = "header",
                    Type = "apiKey"
                });

                options.DescribeAllEnumsAsStrings();
                options.IncludeXmlComments($@"{_hostingEnvironment.ContentRootPath}/Eurofurence.App.Server.Web.xml");


                options.SchemaFilter<IgnoreVirtualPropertiesSchemaFilter>();
                options.OperationFilter<AddAuthorizationHeaderParameterOperationFilter>();
            });


            var oAuthBearerAuthenticationPolicy =
                new AuthorizationPolicyBuilder().AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                    .RequireAuthenticatedUser()
                    .Build();

            services.AddAuthorization(auth =>
            {
                auth.AddPolicy("OAuth-AllAuthenticated", oAuthBearerAuthenticationPolicy);
            });

            var builder = new ContainerBuilder();
            builder.Populate(services);
            builder.RegisterModule(new AutofacModule(database));
            builder.RegisterModule(new Services.DependencyResolution.AutofacModule());
            builder.RegisterInstance(new TokenFactorySettings
            {
                SecretKey = Configuration["oAuth:secretKey"],
                Audience = Configuration["oAuth:audience"],
                Issuer = Configuration["oAuth:issuer"]
            });
            builder.RegisterInstance(new AuthenticationSettings
            {
                ConventionNumber = 23,
                DefaultTokenLifeTime = TimeSpan.FromDays(30)
            });
            builder.RegisterInstance(new WnsConfiguration
            {
                ClientId = Configuration["wns:clientId"],
                ClientSecret = Configuration["wns:clientSecret"],
                TargetTopic = Configuration["wns:targetTopic"]
            });
            builder.RegisterInstance(new FirebaseConfiguration
            {
                AuthorizationKey = Configuration["firebase:authorizationKey"],
                TargetTopic = Configuration["firebase:targetTopic"]
            });
            builder.RegisterInstance(new TelegramConfiguration
            {
                AccessToken = Configuration["telegram:accessToken"],
                Proxy = Configuration["telegram:proxy"]
            });

            builder.Register(c => new ApiPrincipal(c.Resolve<IHttpContextAccessor>().HttpContext.User))
                .As<IApiPrincipal>();

            var container = builder.Build();
            return container.Resolve<IServiceProvider>();
        }

        public void Configure(
            IApplicationBuilder app,
            IHostingEnvironment env,
            ILoggerFactory loggerFactory,
            IApplicationLifetime appLifetime)
        {
            var loggerConfiguration = new LoggerConfiguration().Enrich.FromLogContext();

            if (env.IsDevelopment())
            {
                loggerConfiguration.WriteTo.ColoredConsole();
            }
            else
            {
                var logGroupName = Configuration["aws:cloudwatch:logGroupName"] + "/" + env.EnvironmentName;

                AWSCredentials credentials =
                    new BasicAWSCredentials(Configuration["aws:accessKey"], Configuration["aws:secret"]);
                IAmazonCloudWatchLogs client = new AmazonCloudWatchLogsClient(credentials, RegionEndpoint.EUCentral1);
                var options = new CloudWatchSinkOptions
                {
                    LogGroupName = logGroupName,
                    LogEventRenderer = new CustomLogEventRenderer()
                };

                loggerConfiguration.WriteTo.AmazonCloudWatch(options, client);
            }

            Log.Logger = loggerConfiguration.CreateLogger();
            loggerFactory
                .WithFilter(new FilterLoggerSettings
                {
                    {"Microsoft", env.IsDevelopment() ? LogLevel.Information : LogLevel.Warning},
                    {"System", env.IsDevelopment() ? LogLevel.Information : LogLevel.Warning}
                })
                .AddSerilog();


            appLifetime.ApplicationStopped.Register(Log.CloseAndFlush);

            app.UseCors("CorsPolicy");

            app.UseJwtBearerAuthentication(new JwtBearerOptions
            {
                TokenValidationParameters = new TokenValidationParameters
                {
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.ASCII.GetBytes(Configuration["oAuth:secretKey"])),
                    ValidAudience = Configuration["oAuth:Audience"],
                    ValidIssuer = Configuration["oAuth:Issuer"],
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(0)
                },
                AutomaticAuthenticate = true,
                AutomaticChallenge = true
            });

            app.UseMvc(routes =>
            {
                routes.MapRoute(
                    "default",
                    "{controller=Test}/{action=Index}/{id?}");
            });

            app.UseSwagger();

            app.UseSwaggerUI(c =>
            {
                c.RoutePrefix = "swagger/v2/ui";
                c.DocExpansion("none");
                c.SwaggerEndpoint("/swagger/v2/swagger.json", "API v2");
            });
        }
    }
}