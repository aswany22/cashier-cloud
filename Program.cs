using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
{
	builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

builder.Services.AddSingleton<CashierDatabase>();
builder.Services.AddCors(options =>
{
	options.AddDefaultPolicy(policy => policy.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin());
});

var app = builder.Build();
app.UseCors();

var apiToken = app.Configuration["ApiToken"] ?? "change-me-123";

bool IsAuthorized(HttpRequest request)
{
	if (!request.Headers.TryGetValue("X-Api-Key", out var value))
	{
		return false;
	}

	return string.Equals(value.ToString(), apiToken, StringComparison.Ordinal);
}

IResult Unauthorized() => Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);

app.MapGet("/", () => Results.Ok(new
{
	name = "Cashier Cloud Sync",
	status = "running",
	endpoints = new[] { "/health", "/api/sync/push", "/api/sync/pull", "/api/reports/summary" }
}));

app.MapGet("/health", () => Results.Ok(new
{
	status = "ok",
	serverTime = DateTimeOffset.UtcNow
}));

app.MapPost("/api/sync/push", async (HttpRequest request, CashierDatabase db) =>
{
	if (!IsAuthorized(request))
	{
		return Unauthorized();
	}

	var package = await request.ReadFromJsonAsync<PosSyncPackage>();
	if (package is null)
	{
		return Results.BadRequest(new { error = "invalid_sync_package" });
	}

	var result = await db.MergeAsync(package);
	return Results.Ok(result);
});

app.MapGet("/api/sync/pull", async (HttpRequest request, CashierDatabase db) =>
{
	if (!IsAuthorized(request))
	{
		return Unauthorized();
	}

	var state = await db.ReadAsync();
	return Results.Ok(new PosSyncPackage
	{
		DeviceId = "cloud",
		ExportedAt = DateTime.UtcNow,
		SchemaVersion = 1,
		Products = state.Products,
		Sales = state.Sales
	});
});

app.MapGet("/api/reports/summary", async (HttpRequest request, CashierDatabase db) =>
{
	if (!IsAuthorized(request))
	{
		return Unauthorized();
	}

	var state = await db.ReadAsync();
	var today = DateTime.Today;
	var monthStart = new DateTime(today.Year, today.Month, 1);
	var todaySales = state.Sales.Where(sale => sale.CreatedAt.Date == today).ToList();
	var monthSales = state.Sales.Where(sale => sale.CreatedAt.Date >= monthStart).ToList();

	decimal Profit(Sale sale) => sale.Items.Sum(item => item.LineTotal - item.UnitCost * item.Quantity) - sale.Discount;

	return Results.Ok(new
	{
		todaySales = todaySales.Sum(sale => sale.Total),
		todayProfit = todaySales.Sum(Profit),
		monthSales = monthSales.Sum(sale => sale.Total),
		monthProfit = monthSales.Sum(Profit),
		inventoryValue = state.Products.Sum(product => product.Stock * product.Cost),
		lowStockCount = state.Products.Count(product => product.Stock <= 5),
		productCount = state.Products.Count,
		saleCount = state.Sales.Count,
		lastUpdatedAt = state.UpdatedAt
	});
});

app.Run();

public sealed class CashierDatabase
{
	private readonly string dbPath;
	private readonly SemaphoreSlim gate = new(1, 1);
	private readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };

	public CashierDatabase(IWebHostEnvironment environment)
	{
		var dataDir = Path.Combine(environment.ContentRootPath, "data");
		Directory.CreateDirectory(dataDir);
		dbPath = Path.Combine(dataDir, "cashier-cloud-db.json");
	}

	public async Task<CloudState> ReadAsync()
	{
		await gate.WaitAsync();
		try
		{
			return await ReadUnsafeAsync();
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task<SyncResult> MergeAsync(PosSyncPackage package)
	{
		await gate.WaitAsync();
		try
		{
			var state = await ReadUnsafeAsync();
			var addedProducts = 0;
			var updatedProducts = 0;
			var addedSales = 0;

			foreach (var product in package.Products)
			{
				var existing = state.Products.FirstOrDefault(item => item.Id == product.Id)
					?? state.Products.FirstOrDefault(item => !string.IsNullOrWhiteSpace(product.Barcode)
						&& item.Barcode.Equals(product.Barcode, StringComparison.OrdinalIgnoreCase));

				if (existing is null)
				{
					state.Products.Add(product);
					addedProducts++;
					continue;
				}

				if (product.UpdatedAt >= existing.UpdatedAt)
				{
					existing.Name = product.Name;
					existing.Barcode = product.Barcode;
					existing.Category = product.Category;
					existing.Price = product.Price;
					existing.Cost = product.Cost;
					existing.Stock = product.Stock;
					existing.UpdatedAt = product.UpdatedAt;
					updatedProducts++;
				}
			}

			foreach (var sale in package.Sales)
			{
				if (state.Sales.Any(item => item.Id == sale.Id))
				{
					continue;
				}

				sale.IsSynced = true;
				state.Sales.Add(sale);
				addedSales++;
			}

			state.UpdatedAt = DateTime.UtcNow;
			await WriteUnsafeAsync(state);

			return new SyncResult
			{
				AddedProducts = addedProducts,
				UpdatedProducts = updatedProducts,
				AddedSales = addedSales,
				ServerTime = DateTime.UtcNow
			};
		}
		finally
		{
			gate.Release();
		}
	}

	private async Task<CloudState> ReadUnsafeAsync()
	{
		if (!File.Exists(dbPath))
		{
			return new CloudState();
		}

		await using var stream = File.OpenRead(dbPath);
		return await JsonSerializer.DeserializeAsync<CloudState>(stream) ?? new CloudState();
	}

	private async Task WriteUnsafeAsync(CloudState state)
	{
		var tempPath = dbPath + ".tmp";
		await using (var stream = File.Create(tempPath))
		{
			await JsonSerializer.SerializeAsync(stream, state, jsonOptions);
		}

		File.Move(tempPath, dbPath, overwrite: true);
	}
}

public sealed class SyncResult
{
	public int AddedProducts { get; set; }
	public int UpdatedProducts { get; set; }
	public int AddedSales { get; set; }
	public DateTime ServerTime { get; set; }
}

public sealed class CloudState
{
	public List<Product> Products { get; set; } = [];
	public List<Sale> Sales { get; set; } = [];
	public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class PosSyncPackage
{
	public string DeviceId { get; set; } = string.Empty;
	public DateTime ExportedAt { get; set; }
	public int SchemaVersion { get; set; }
	public List<Product> Products { get; set; } = [];
	public List<Sale> Sales { get; set; } = [];
}

public sealed class Product
{
	public string Id { get; set; } = string.Empty;
	public string Name { get; set; } = string.Empty;
	public string Barcode { get; set; } = string.Empty;
	public string Category { get; set; } = string.Empty;
	public decimal Price { get; set; }
	public decimal Cost { get; set; }
	public decimal Stock { get; set; }
	public DateTime UpdatedAt { get; set; }
}

public sealed class Sale
{
	public string Id { get; set; } = string.Empty;
	public string DeviceId { get; set; } = string.Empty;
	public string InvoiceNumber { get; set; } = string.Empty;
	public DateTime CreatedAt { get; set; }
	public string CustomerName { get; set; } = string.Empty;
	public string PaymentMethod { get; set; } = string.Empty;
	public decimal Subtotal { get; set; }
	public decimal Discount { get; set; }
	public decimal Tax { get; set; }
	public decimal Total { get; set; }
	public bool IsSynced { get; set; }
	public int SyncVersion { get; set; }
	public List<SaleItem> Items { get; set; } = [];
}

public sealed class SaleItem
{
	public string ProductId { get; set; } = string.Empty;
	public string Name { get; set; } = string.Empty;
	public decimal Quantity { get; set; }
	public decimal UnitPrice { get; set; }
	public decimal UnitCost { get; set; }
	public decimal LineTotal { get; set; }
}
