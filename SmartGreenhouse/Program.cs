using SmartGreenhouse.Services;
Iot.Device.Graphics.SkiaSharpAdapter.SkiaSharpAdapter.Register();
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddSingleton<HardwareService>();
var app = builder.Build();
app.UseDefaultFiles(); // Позволяет серверу искать index.html по умолчанию
app.UseStaticFiles();  // Разрешает раздачу файлов из папки wwwroot
// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthorization();

app.MapControllers();

app.Run();
