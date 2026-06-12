using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Demo.Models;
using WalkingTec.Mvvm.Demo.Models._Admin;
using WalkingTec.Mvvm.Demo.Models.Virus;
using WalkingTec.Mvvm.Demo.Models.ECommerce;
using WalkingTec.Mvvm.Etl;
using WalkingTec.Mvvm.WorkFlow; // FIX-B2: ApplyWorkFlowModels extension

namespace WalkingTec.Mvvm.Demo
{
    public class DataContext : FrameworkContext
    {
        public DataContext(CS cs)
             : base(cs)
        {
        }

        public DataContext(string cs, DBTypeEnum dbtype)
            : base(cs, dbtype)
        {
        }

        public DataContext(string cs, DBTypeEnum dbtype, string version = null)
            : base(cs, dbtype, version)
        {
        }

        public DataContext(DbContextOptions<DataContext> options) : base(options) { }
        public DbSet<FrameworkUser> FrameworkUsers { get; set; }

        public DbSet<Major> Majors { get; set; }
        public DbSet<School> Schools { get; set; }
        public DbSet<Student> Students { get; set; }
        public DbSet<City> Cities { get; set; }
        public DbSet<WxReportData> WxReportDatas { get; set; }
        public DbSet<不要用中文模型名> 不要中文 { get; set; }
        public DbSet<ISOType> ISOTypes { get; set; }
        public DbSet<SoftFacInfo> SoftFacInfos { get; set; }
        public DbSet<Virus> Viruses { get; set; }
        public DbSet<Hospital> Hospitals { get; set; }
        public DbSet<ControlCenter> ControlCenters { get; set; }
        public DbSet<Patient> Patients { get; set; }
        public DbSet<Report> Reports { get; set; }
        public DbSet<LinkTest> LinkTests { get; set; }
        public DbSet<LinkTest2> LinkTest2 { get; set; }
        public DbSet<TreeTest> TreeTests { get; set; }
        public DbSet<MyGroup> MyGroups { get; set; }
        public DbSet<MyTenant> MyTenants { get; set; }

        // E-Commerce
        public DbSet<Product> Products { get; set; }
        public DbSet<Customer> Customers { get; set; }
        public DbSet<Order> Orders { get; set; }
        public DbSet<OrderItem> OrderItems { get; set; }
        public override async Task<bool> DataInit(object allModules, bool IsSpa)
        {
            var state = await base.DataInit(allModules, IsSpa);
            bool emptydb = false;

            try
            {
                emptydb = Set<FrameworkUser>().Count() == 0 && Set<FrameworkUserRole>().Count() == 0;
            }
            catch { }

           
            if (state == true || emptydb == true)
            {
                //when state is true, means it's the first time EF create database, do data init here
                //当state是true的时候，表示这是第一次创建数据库，可以在这里进行数据初始化
                var user = new FrameworkUser
                {
                    ITCode = "admin",
                    Password = PasswordHashHelper.HashPassword("000000"),
                    IsValid = true,
                    Name = "Admin",
                    TenantCode = TenantCode
                };

                var userrole = new FrameworkUserRole
                {
                    UserCode = user.ITCode,
                    RoleCode = "001",
                    TenantCode= TenantCode
                };
                Set<FrameworkUser>().Add(user);
                Set<FrameworkUserRole>().Add(userrole);
                await SaveChangesAsync();

                Dictionary<string, List<object>> data = new Dictionary<string, List<object>>();
                SetTestData(typeof(School), data, 50);
                SetTestData(typeof(Major), data, 100);
                SetTestData(typeof(Student), data, 500);
                SetTestData(typeof(City), data, 1000);
                SetTestData(typeof(ControlCenter), data);
                SetTestData(typeof(Hospital), data);
                SetTestData(typeof(Patient), data);
                SetTestData(typeof(Virus), data);
                SetTestData(typeof(Report), data);

                SeedECommerceData();
            }
            return state;
        }

        private void SetTestData(Type modelType, Dictionary<string, List<object>> data, int count = 100)
        {
            if (data.ContainsKey(modelType.FullName) && data[modelType.FullName].Count>=count)
            {
                return;
            }
            using (var dc = this.CreateNew())
            {
                Random r = new Random();
                data[modelType.FullName] = new List<object>();
                int retry = 0;
                List<string> ids = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    var modelprops = modelType.GetRandomValuesForTestData();
                    var newobj = modelType.GetConstructor(Type.EmptyTypes).Invoke(null);
                    var idvalue = modelprops.Where(x => x.Key == "ID").Select(x=>x.Value).SingleOrDefault();
                    if (idvalue != null )
                    {
                        if (ids.Contains(idvalue.ToLower()) == false)
                        {
                            ids.Add(idvalue.ToLower());
                        }
                        else
                        {
                            retry++;
                            i--;
                            if (retry > count)
                            {
                                break;
                            }
                            continue;
                        }
                    }
                    foreach (var pro in modelprops)
                    {
                        if (pro.Value == "$fk$")
                        {
                            var fktype = modelType.GetSingleProperty(pro.Key[0..^2])?.PropertyType;
                            if (fktype != modelType)
                            {
                                SetTestData(fktype, data);
                                newobj.SetPropertyValue(pro.Key, (data[fktype.FullName][r.Next(0, data[fktype.FullName].Count)] as TopBasePoco).GetID());
                            }
                        }
                        else
                        {
                            var v = pro.Value;
                            if (v.StartsWith("\""))
                            {
                                v = v[1..];
                            }
                            if (v.EndsWith("\""))
                            {
                                v = v[..^1];
                            }
                            newobj.SetPropertyValue(pro.Key, v);
                        }
                    }
                    if(modelType == typeof(FileAttachment))
                    {
                        newobj.SetPropertyValue("Path", "./wwwroot/logo.png");
                        newobj.SetPropertyValue("SaveMode", "local");
                        newobj.SetPropertyValue("Length", 16728);
                    }
                    try
                    {
                        (dc as DbContext).Add(newobj);
                        data[modelType.FullName].Add(newobj);
                    }
                    catch
                    {
                        retry++;
                        i--;
                        if(retry > count)
                        {
                            break;
                        }
                    }
                }
                int a = dc.SaveChanges();
            }
        }

        private void SeedECommerceData()
        {
            var r = new Random(42); // fixed seed for reproducible data

            // --- Products: 48 items across 6 categories ---
            var brands = new Dictionary<ProductCategory, string[]>
            {
                [ProductCategory.Electronics] = new[] { "Apple", "Samsung", "Sony", "ASUS" },
                [ProductCategory.Clothing]    = new[] { "Uniqlo", "ZARA", "Nike", "Adidas" },
                [ProductCategory.Food]        = new[] { "義美", "統一", "味全", "桂格" },
                [ProductCategory.Home]        = new[] { "IKEA", "無印良品", "HOLA", "特力屋" },
                [ProductCategory.Sports]      = new[] { "Nike", "Adidas", "Under Armour", "Puma" },
                [ProductCategory.Books]       = new[] { "天下", "遠流", "商周", "城邦" },
            };
            var productNames = new Dictionary<ProductCategory, string[]>
            {
                [ProductCategory.Electronics] = new[] { "無線耳機", "平板電腦", "智慧手錶", "行動電源", "藍牙喇叭", "充電器", "滑鼠", "鍵盤" },
                [ProductCategory.Clothing]    = new[] { "T恤", "牛仔褲", "外套", "洋裝", "運動褲", "襯衫", "針織衫", "風衣" },
                [ProductCategory.Food]        = new[] { "堅果禮盒", "咖啡豆", "有機茶葉", "餅乾組合", "巧克力", "果醬", "麥片", "蜂蜜" },
                [ProductCategory.Home]        = new[] { "收納盒", "檯燈", "靠枕", "地毯", "掛鐘", "花瓶", "相框", "香氛蠟燭" },
                [ProductCategory.Sports]      = new[] { "瑜伽墊", "運動水壺", "啞鈴組", "跳繩", "護膝", "運動背包", "泡棉滾筒", "阻力帶" },
                [ProductCategory.Books]       = new[] { "程式設計入門", "商業策略", "心理學概論", "小說精選", "投資理財", "料理食譜", "歷史故事", "科普讀物" },
            };
            var priceRanges = new Dictionary<ProductCategory, (int min, int max)>
            {
                [ProductCategory.Electronics] = (500, 15000),
                [ProductCategory.Clothing]    = (300, 3000),
                [ProductCategory.Food]        = (100, 800),
                [ProductCategory.Home]        = (200, 5000),
                [ProductCategory.Sports]      = (200, 3000),
                [ProductCategory.Books]       = (200, 600),
            };

            var products = new List<Product>();
            foreach (ProductCategory cat in Enum.GetValues(typeof(ProductCategory)))
            {
                var catBrands = brands[cat];
                var catNames = productNames[cat];
                var (pmin, pmax) = priceRanges[cat];
                for (int i = 0; i < catNames.Length; i++)
                {
                    products.Add(new Product
                    {
                        ID = Guid.NewGuid(),
                        Name = catNames[i],
                        Brand = catBrands[i % catBrands.Length],
                        Category = cat,
                        UnitPrice = r.Next(pmin, pmax),
                    });
                }
            }
            Set<Product>().AddRange(products);
            SaveChanges();

            // --- Customers: 200 across 4 regions ---
            var lastNames = new[] { "陳", "林", "黃", "張", "李", "王", "吳", "劉", "蔡", "楊" };
            var firstNames = new[] { "怡君", "志明", "雅婷", "建宏", "淑芬", "俊傑", "美玲", "家豪", "佳蓉", "宗翰" };
            var regions = (Region[])Enum.GetValues(typeof(Region));
            var tiers = (CustomerTier[])Enum.GetValues(typeof(CustomerTier));

            var customers = new List<Customer>();
            for (int i = 0; i < 200; i++)
            {
                customers.Add(new Customer
                {
                    ID = Guid.NewGuid(),
                    Name = lastNames[r.Next(lastNames.Length)] + firstNames[r.Next(firstNames.Length)],
                    Region = regions[r.Next(regions.Length)],
                    // 60% Normal, 30% VIP, 10% VVIP
                    Tier = r.Next(100) < 60 ? CustomerTier.Normal : r.Next(100) < 75 ? CustomerTier.VIP : CustomerTier.VVIP,
                });
            }
            Set<Customer>().AddRange(customers);
            SaveChanges();

            // --- Orders: 2000 spanning 6 months ---
            var statuses = (OrderStatus[])Enum.GetValues(typeof(OrderStatus));
            var payments = (PaymentMethod[])Enum.GetValues(typeof(PaymentMethod));
            var baseDate = new DateTime(2025, 10, 1);

            var orders = new List<Order>();
            var allItems = new List<OrderItem>();
            int orderSeq = 1;

            for (int i = 0; i < 2000; i++)
            {
                var customer = customers[r.Next(customers.Count)];
                var dayOffset = r.Next(0, 180); // 6 months
                var orderDate = baseDate.AddDays(dayOffset).AddHours(r.Next(8, 22)).AddMinutes(r.Next(60));

                // Weight statuses: 70% completed, 10% shipped, 8% pending, 7% cancelled, 5% returned
                var statusRoll = r.Next(100);
                var status = statusRoll < 70 ? OrderStatus.Completed
                    : statusRoll < 80 ? OrderStatus.Shipped
                    : statusRoll < 88 ? OrderStatus.Pending
                    : statusRoll < 95 ? OrderStatus.Cancelled
                    : OrderStatus.Returned;

                var order = new Order
                {
                    ID = Guid.NewGuid(),
                    OrderNo = $"ORD-{orderSeq++:D5}",
                    CustomerId = customer.ID,
                    OrderDate = orderDate,
                    Status = status,
                    PaymentMethod = payments[r.Next(payments.Length)],
                    TotalAmount = 0,
                };

                // 1-5 items per order
                int itemCount = r.Next(1, 6);
                decimal total = 0;
                var usedProducts = new HashSet<int>();

                for (int j = 0; j < itemCount; j++)
                {
                    int pIdx;
                    do { pIdx = r.Next(products.Count); } while (usedProducts.Contains(pIdx));
                    usedProducts.Add(pIdx);

                    var product = products[pIdx];
                    int qty = r.Next(1, 6);
                    // Slight price variation (+/- 10%)
                    decimal price = Math.Round(product.UnitPrice * (0.9m + (decimal)r.NextDouble() * 0.2m));
                    decimal subtotal = price * qty;
                    total += subtotal;

                    allItems.Add(new OrderItem
                    {
                        ID = Guid.NewGuid(),
                        OrderId = order.ID,
                        ProductId = product.ID,
                        Quantity = qty,
                        UnitPrice = price,
                        Subtotal = subtotal,
                    });
                }

                order.TotalAmount = total;
                orders.Add(order);
            }

            Set<Order>().AddRange(orders);
            SaveChanges();

            Set<OrderItem>().AddRange(allItems);
            SaveChanges();
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ApplyEtlModels();
            // FIX-B2: register WorkFlow models so the designer store can use this DB context.
            modelBuilder.ApplyWorkFlowModels();
        }
    }

    public class DataContextFactory : IDesignTimeDbContextFactory<DataContext>
    {
        public DataContext CreateDbContext(string[] args)
        {
            return new DataContext("你的完整连接字符串", DBTypeEnum.SqlServer);
        }
    }
}
