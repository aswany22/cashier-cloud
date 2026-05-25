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
var apiJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
	PropertyNameCaseInsensitive = true
};

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

	var document = await request.ReadFromJsonAsync<JsonDocument>();
	if (document is null)
	{
		return Results.BadRequest(new { error = "invalid_sync_package" });
	}

if (document.RootElement.TryGetProperty("Items", out _) ||
	document.RootElement.TryGetProperty("items", out _))
	{
		var outboxRequest = document.Deserialize<OutboxPushRequest>(apiJsonOptions);
		if (outboxRequest is null)
		{
			return Results.BadRequest(new { error = "invalid_outbox_package" });
		}

		var outboxResult = await db.MergeOutboxAsync(outboxRequest);
		return Results.Ok(outboxResult);
	}

	var package = document.Deserialize<PosSyncPackage>(apiJsonOptions);
	if (package is null)
	{
		return Results.BadRequest(new { error = "invalid_pos_package" });
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

app.MapGet("/api/dashboard/summary", async (HttpRequest request, CashierDatabase db) =>
{
	var apiKey = request.Query["apiKey"].ToString();
	if (!string.Equals(apiKey, apiToken, StringComparison.Ordinal))
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

	public async Task<OutboxPushResponse> MergeOutboxAsync(OutboxPushRequest request)
	{
		await gate.WaitAsync();
		try
		{
			var state = await ReadUnsafeAsync();
			var accepted = new List<long>();
			var failed = new List<long>();

			foreach (var item in request.Items)
			{
				try
				{
					switch (item.EntityName)
					{
						case "Products":
							ApplyProductOutboxItem(state, item);
							accepted.Add(item.SyncId);
							break;
						case "SalesInvoice":
							ApplySalesInvoiceOutboxItem(state, item, request.DeviceId);
							accepted.Add(item.SyncId);
							break;
						default:
							failed.Add(item.SyncId);
							break;
					}
				}
				catch
				{
					failed.Add(item.SyncId);
				}
			}

			state.UpdatedAt = DateTime.UtcNow;
			await WriteUnsafeAsync(state);

			return new OutboxPushResponse
			{
				Success = failed.Count == 0,
				AcceptedSyncIds = accepted,
				FailedSyncIds = failed
			};
		}
		finally
		{
			gate.Release();
		}
	}

	private static void ApplyProductOutboxItem(CloudState state, CloudSyncItem item)
	{
		using var document = JsonDocument.Parse(item.PayloadJson);
		var root = document.RootElement;

		var productId = GetString(root, "ProductId", item.EntityId ?? string.Empty);
		var barcode = GetString(root, "Barcode");

		if (string.Equals(item.ActionName, "Delete", StringComparison.OrdinalIgnoreCase))
		{
			state.Products.RemoveAll(product => product.Id == productId || (!string.IsNullOrWhiteSpace(barcode) && product.Barcode == barcode));
			return;
		}

		var existing = state.Products.FirstOrDefault(product => product.Id == productId)
			?? state.Products.FirstOrDefault(product => !string.IsNullOrWhiteSpace(barcode)
				&& product.Barcode.Equals(barcode, StringComparison.OrdinalIgnoreCase));

		var updatedAt = GetDateTime(root, "UpdatedAtUtc", DateTime.UtcNow);
		if (existing is null)
		{
			state.Products.Add(new Product
			{
				Id = productId,
				Name = FirstNonEmpty(GetString(root, "NameAr"), GetString(root, "NameEn"), barcode, productId),
				Barcode = barcode,
				Category = GetString(root, "Category", "عام"),
				Price = GetDecimal(root, "Price"),
				Cost = GetDecimal(root, "CostPrice"),
				Stock = GetDecimal(root, "Quantity"),
				UpdatedAt = updatedAt
			});
			return;
		}

		if (updatedAt < existing.UpdatedAt)
		{
			return;
		}

		existing.Name = FirstNonEmpty(GetString(root, "NameAr"), GetString(root, "NameEn"), existing.Name);
		existing.Barcode = barcode;
		existing.Category = GetString(root, "Category", existing.Category);
		existing.Price = GetDecimal(root, "Price");
		existing.Cost = GetDecimal(root, "CostPrice");
		existing.Stock = GetDecimal(root, "Quantity");
		existing.UpdatedAt = updatedAt;
	}

	private static void ApplySalesInvoiceOutboxItem(CloudState state, CloudSyncItem item, string deviceId)
	{
		using var document = JsonDocument.Parse(item.PayloadJson);
		var root = document.RootElement;
		var invoiceId = GetString(root, "InvoiceId", item.EntityId ?? string.Empty);
		var isNewSale = state.Sales.All(sale => sale.Id != invoiceId);

		state.Sales.RemoveAll(sale => sale.Id == invoiceId);

		var items = new List<SaleItem>();
		if (root.TryGetProperty("Items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array)
		{
			foreach (var line in itemsElement.EnumerateArray())
			{
				var barcode = GetString(line, "Barcode");
				var productName = GetString(line, "ProductName", barcode);
				var quantity = GetDecimal(line, "Quantity");
				var price = GetDecimal(line, "Price");
				var unitCost = GetDecimal(line, "PurchasePrice");
				var discount = GetDecimal(line, "Discount");
				var lineTotal = GetDecimal(line, "Total", quantity * price - discount);

				items.Add(new SaleItem
				{
					ProductId = barcode,
					Name = productName,
					Quantity = quantity,
					UnitPrice = price,
					UnitCost = unitCost,
					LineTotal = lineTotal
				});

				var product = state.Products.FirstOrDefault(product => !string.IsNullOrWhiteSpace(barcode)
					&& product.Barcode.Equals(barcode, StringComparison.OrdinalIgnoreCase));
				if (product is not null && isNewSale)
				{
					product.Stock -= quantity;
					product.UpdatedAt = DateTime.UtcNow;
				}
			}
		}

		state.Sales.Add(new Sale
		{
			Id = invoiceId,
			DeviceId = deviceId,
			InvoiceNumber = GetString(root, "InvoiceNumber", invoiceId).TrimStart('#'),
			CreatedAt = GetDateTime(root, "InvoiceDate", DateTime.UtcNow),
			CustomerName = GetString(root, "CustomerName"),
			PaymentMethod = GetString(root, "PaymentType", "Cash"),
			Subtotal = items.Sum(line => line.LineTotal),
			Discount = 0,
			Tax = 0,
			Total = GetDecimal(root, "NetAmount", items.Sum(line => line.LineTotal)),
			IsSynced = true,
			SyncVersion = 1,
			Items = items
		});
	}

	private static string GetString(JsonElement element, string propertyName, string defaultValue = "")
	{
		if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
		{
			return defaultValue;
		}

		return property.ToString() ?? defaultValue;
	}

	private static decimal GetDecimal(JsonElement element, string propertyName, decimal defaultValue = 0)
	{
		if (!element.TryGetProperty(propertyName, out var property))
		{
			return defaultValue;
		}

		if (property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out var value))
		{
			return value;
		}

		return decimal.TryParse(property.ToString(), out var parsed) ? parsed : defaultValue;
	}

	private static DateTime GetDateTime(JsonElement element, string propertyName, DateTime defaultValue)
	{
		if (!element.TryGetProperty(propertyName, out var property))
		{
			return defaultValue;
		}

		return DateTime.TryParse(property.ToString(), out var parsed) ? parsed : defaultValue;
	}

	private static string FirstNonEmpty(params string[] values)
	{
		return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
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

public sealed class OutboxPushRequest
{
	public string DeviceId { get; set; } = string.Empty;
	public List<CloudSyncItem> Items { get; set; } = [];
}

public sealed class CloudSyncItem
{
	public long SyncId { get; set; }
	public string EntityName { get; set; } = string.Empty;
	public string? EntityId { get; set; }
	public string ActionName { get; set; } = string.Empty;
	public string PayloadJson { get; set; } = string.Empty;
	public string CreatedAt { get; set; } = string.Empty;
}

public sealed class OutboxPushResponse
{
	public bool Success { get; set; }
	public List<long> AcceptedSyncIds { get; set; } = [];
	public List<long> FailedSyncIds { get; set; } = [];
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
