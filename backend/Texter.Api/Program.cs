using System.Text.Json.Serialization;
using Texter.Logic;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddTexter(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.MapControllers();

app.Run();
