using Microsoft.AspNet.Identity;
using Microsoft.AspNet.Identity.Owin;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.Entity;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using WebBanHangOnline.Models;
using WebBanHangOnline.Models.EF;
using WebBanHangOnline.Models.Payments;
using Microsoft.AspNet.SignalR;
using WebBanHangOnline.Hubs;

namespace WebBanHangOnline.Controllers
{
    [System.Web.Mvc.Authorize]
    public class ShoppingCartController : Controller
    {
        private ApplicationDbContext db = new ApplicationDbContext();
        private readonly IHubContext _hubContext;

        private ApplicationSignInManager _signInManager;
        private ApplicationUserManager _userManager;

        public ShoppingCartController()
        {
            _hubContext = GlobalHost.ConnectionManager.GetHubContext<StockHub>();
        }

        public ShoppingCartController(ApplicationUserManager userManager, ApplicationSignInManager signInManager)
        {
            UserManager = userManager;
            SignInManager = signInManager;
            _hubContext = GlobalHost.ConnectionManager.GetHubContext<StockHub>();
        }

        public ApplicationSignInManager SignInManager
        {
            get { return _signInManager ?? HttpContext.GetOwinContext().Get<ApplicationSignInManager>(); }
            private set { _signInManager = value; }
        }

        public ApplicationUserManager UserManager
        {
            get { return _userManager ?? HttpContext.GetOwinContext().GetUserManager<ApplicationUserManager>(); }
            private set { _userManager = value; }
        }

        // Helper method để lấy hoặc khởi tạo giỏ hàng
        private ShoppingCart GetCart()
        {
            if (Session["Cart"] == null)
            {
                Session["Cart"] = new ShoppingCart();
            }
            return (ShoppingCart)Session["Cart"];
        }

        [AllowAnonymous]
        public ActionResult Index()
        {
            var cart = GetCart();
            if (cart.Items.Any())
            {
                ViewBag.CheckCart = cart;
            }
            return View();
        }

        [AllowAnonymous]
        public ActionResult VnpayReturn()
        {
            if (Request.QueryString.Count > 0)
            {
                string vnp_HashSecret = ConfigurationManager.AppSettings["vnp_HashSecret"];
                var vnpayData = Request.QueryString;
                VnPayLibrary vnpay = new VnPayLibrary();

                foreach (string s in vnpayData)
                {
                    if (!string.IsNullOrEmpty(s) && s.StartsWith("vnp_"))
                    {
                        vnpay.AddResponseData(s, vnpayData[s]);
                    }
                }

                string orderCode = Convert.ToString(vnpay.GetResponseData("vnp_TxnRef"));
                string vnp_ResponseCode = vnpay.GetResponseData("vnp_ResponseCode");
                string vnp_TransactionStatus = vnpay.GetResponseData("vnp_TransactionStatus");
                string vnp_SecureHash = Request.QueryString["vnp_SecureHash"];

                bool checkSignature = vnpay.ValidateSignature(vnp_SecureHash, vnp_HashSecret);
                if (checkSignature)
                {
                    if (vnp_ResponseCode == "00" && vnp_TransactionStatus == "00")
                    {
                        var itemOrder = db.Orders.FirstOrDefault(x => x.Code == orderCode);
                        if (itemOrder != null && itemOrder.Status == 1) // Chưa thanh toán
                        {
                            itemOrder.Status = 2; // Đã thanh toán
                            db.Entry(itemOrder).State = EntityState.Modified;
                            db.SaveChanges();
                        }
                        ViewBag.InnerText = "Giao dịch được thực hiện thành công. Cảm ơn quý khách!";
                    }
                    else
                    {
                        ViewBag.InnerText = "Có lỗi xảy ra. Mã lỗi: " + vnp_ResponseCode;
                    }
                    ViewBag.ThanhToanThanhCong = "Số tiền thanh toán (VND): " + (Convert.ToInt64(vnpay.GetResponseData("vnp_Amount")) / 100).ToString();
                }
            }
            return View();
        }

        [AllowAnonymous]
        public ActionResult CheckOut()
        {
            var cart = GetCart();
            if (cart.Items.Any())
            {
                ViewBag.CheckCart = cart;
            }
            return View();
        }

        [AllowAnonymous]
        public ActionResult CheckOutSuccess()
        {
            return View();
        }

        [AllowAnonymous]
        public ActionResult Partial_Item_ThanhToan()
        {
            var cart = GetCart();
            return PartialView(cart.Items);
        }

        [AllowAnonymous]
        public ActionResult Partial_Item_Cart()
        {
            var cart = GetCart();
            return PartialView(cart.Items);
        }

        [AllowAnonymous]
        public ActionResult ShowCount()
        {
            var cart = GetCart();
            return Json(new { Count = cart.Items.Count }, JsonRequestBehavior.AllowGet);
        }

        [AllowAnonymous]
        public ActionResult Partial_CheckOut()
        {
            var user = UserManager.FindByNameAsync(User.Identity.Name).Result;
            if (user != null)
            {
                ViewBag.User = user;
            }
            return PartialView();
        }
        //Định nghĩa GenerateOrderCode
        private string GenerateOrderCode()
        {
            // Tạo mã đơn hàng ngẫu nhiên (ví dụ: "DH" + 4 số ngẫu nhiên)
            var rd = new Random();
            string code = "DH" + rd.Next(1000, 9999).ToString();

            // Kiểm tra trùng lặp (nếu cần)
            while (db.Orders.Any(x => x.Code == code))
            {
                code = "DH" + rd.Next(1000, 9999).ToString();
            }

            return code;
        }
        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public ActionResult CheckOut(OrderViewModel req)
        {
            if (!ModelState.IsValid)
                return Json(new { Success = false, msg = "Thông tin đơn hàng không hợp lệ." });

            var cart = GetCart();
            if (!cart.Items.Any())
                return Json(new { Success = false, msg = "Giỏ hàng trống." });

            using (var transaction = db.Database.BeginTransaction())
            {
                try
                {
                    // 1. Khóa dòng (UPDLOCK) để tránh oversell
                    var productsToUpdate = new List<Product>();
                    foreach (var item in cart.Items)
                    {
                        var sql = @"
                    SELECT *
                    FROM dbo.Products WITH (UPDLOCK, ROWLOCK)
                    WHERE Id = @p0";
                        var product = db.Products.SqlQuery(sql, item.ProductId).FirstOrDefault();

                        if (product == null)
                            throw new InvalidOperationException($"Sản phẩm Id={item.ProductId} không tồn tại.");

                        if (product.Quantity < item.Quantity)
                            throw new InvalidOperationException($"'{item.ProductName}' chỉ còn {product.Quantity} trong kho.");

                        // Giảm tồn tạm thời
                        product.Quantity -= item.Quantity;
                        productsToUpdate.Add(product);
                    }

                    // 2. Tạo Order + OrderDetails
                    var order = new Order
                    {
                        // Tự sinh mã đơn hàng, ví dụ: DH1234
                        Code = GenerateOrderCode(),
                        CustomerName = req.CustomerName,
                        Phone = req.Phone,
                        Address = req.Address,
                        Email = req.Email,
                        Status = 1, // Chưa thanh toán
                        TotalAmount = cart.Items.Sum(x => x.Price * x.Quantity),
                        Quantity = cart.Items.Sum(x => x.Quantity),
                        TypePayment = req.TypePayment,
                        CreatedDate = DateTime.Now,
                        ModifiedDate = DateTime.Now,
                        CustomerId = User.Identity.IsAuthenticated ? User.Identity.GetUserId() : null
                    };
                    foreach (var it in cart.Items)
                    {
                        order.OrderDetails.Add(new OrderDetail
                        {
                            ProductId = it.ProductId,
                            Quantity = it.Quantity,
                            Price = it.Price
                        });
                    }
                    db.Orders.Add(order);

                    // 3. Lưu Order và cập nhật kho
                    db.SaveChanges();
                    transaction.Commit();

                    // 4. Cập nhật lại Products và gửi realtime
                    foreach (var p in productsToUpdate)
                    {
                        db.Entry(p).State = EntityState.Modified;
                    }
                    db.SaveChanges();
                    foreach (var p in productsToUpdate)
                    {
                        _hubContext.Clients.All.ReceiveStockUpdate(p.Id, p.Quantity);
                    }

                    // 5. Xóa giỏ và trả về
                    cart.ClearCart();
                    return Json(new { Success = true, msg = "Đặt hàng thành công!" });
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    return Json(new { Success = false, msg = ex.Message });
                }
            }
        }

        [AllowAnonymous]
        [HttpPost]
        public ActionResult AddToCart(int id, int quantity)
        {
            var code = new { Success = false, msg = "", code = -1, Count = 0 };
            var db = new ApplicationDbContext();
            var checkProduct = db.Products.FirstOrDefault(x => x.Id == id);
            if (checkProduct != null)
            {
                ShoppingCart cart = (ShoppingCart)Session["Cart"];
                if (cart == null)
                {
                    cart = new ShoppingCart();
                }
                ShoppingCartItem item = new ShoppingCartItem
                {
                    ProductId = checkProduct.Id,
                    ProductName = checkProduct.Title,
                    CategoryName = checkProduct.ProductCategory.Title,
                    Alias = checkProduct.Alias,
                    Quantity = quantity
                };
                if (checkProduct.ProductImage.FirstOrDefault(x => x.IsDefault) != null)
                {
                    item.ProductImg = checkProduct.ProductImage.FirstOrDefault(x => x.IsDefault).Image;
                }
                item.Price = checkProduct.Price;
                if (checkProduct.PriceSale > 0)
                {
                    item.Price = (decimal)checkProduct.PriceSale;
                }
                item.TotalPrice = item.Quantity * item.Price;
                cart.AddToCart(item, quantity);
                Session["Cart"] = cart;
                code = new { Success = true, msg = "Thêm sản phẩm vào giở hàng thành công!", code = 1, Count = cart.Items.Count };
            }
            return Json(code);
        }

        [AllowAnonymous]
        [HttpPost]
        public ActionResult Update(int id, int quantity)
        {
            var cart = GetCart();
            if (cart != null)
            {
                cart.UpdateQuantity(id, quantity);
                return Json(new { Success = true });
            }
            return Json(new { Success = false });
        }

        [AllowAnonymous]
        [HttpPost]
        public ActionResult Delete(int id)
        {
            var code = new { Success = false, msg = "", code = -1, Count = 0 };
            var cart = GetCart();
            if (cart != null)
            {
                var checkProduct = cart.Items.FirstOrDefault(x => x.ProductId == id);
                if (checkProduct != null)
                {
                    cart.Remove(id);
                    code = new { Success = true, msg = "", code = 1, Count = cart.Items.Count };
                }
            }
            return Json(code);
        }

        [AllowAnonymous]
        [HttpPost]
        public ActionResult DeleteAll()
        {
            var cart = GetCart();
            if (cart != null)
            {
                cart.ClearCart();
                return Json(new { Success = true });
            }
            return Json(new { Success = false });
        }

        #region Thanh toán VNPay
        public string UrlPayment(int TypePaymentVN, string orderCode)
        {
            var urlPayment = "";
            var order = db.Orders.FirstOrDefault(x => x.Code == orderCode);
            string vnp_Returnurl = ConfigurationManager.AppSettings["vnp_Returnurl"];
            string vnp_Url = ConfigurationManager.AppSettings["vnp_Url"];
            string vnp_TmnCode = ConfigurationManager.AppSettings["vnp_TmnCode"];
            string vnp_HashSecret = ConfigurationManager.AppSettings["vnp_HashSecret"];

            VnPayLibrary vnpay = new VnPayLibrary();
            var Price = (long)order.TotalAmount * 100;
            vnpay.AddRequestData("vnp_Version", VnPayLibrary.VERSION);
            vnpay.AddRequestData("vnp_Command", "pay");
            vnpay.AddRequestData("vnp_TmnCode", vnp_TmnCode);
            vnpay.AddRequestData("vnp_Amount", Price.ToString());
            if (TypePaymentVN == 1)
            {
                vnpay.AddRequestData("vnp_BankCode", "VNPAYQR");
            }
            else if (TypePaymentVN == 2)
            {
                vnpay.AddRequestData("vnp_BankCode", "VNBANK");
            }
            else if (TypePaymentVN == 3)
            {
                vnpay.AddRequestData("vnp_BankCode", "INTCARD");
            }

            vnpay.AddRequestData("vnp_CreateDate", order.CreatedDate.ToString("yyyyMMddHHmmss"));
            vnpay.AddRequestData("vnp_CurrCode", "VND");
            vnpay.AddRequestData("vnp_IpAddr", Utils.GetIpAddress());
            vnpay.AddRequestData("vnp_Locale", "vn");
            vnpay.AddRequestData("vnp_OrderInfo", "Thanh toán đơn hàng: " + order.Code);
            vnpay.AddRequestData("vnp_OrderType", "other");
            vnpay.AddRequestData("vnp_ReturnUrl", vnp_Returnurl);
            vnpay.AddRequestData("vnp_TxnRef", order.Code);

            urlPayment = vnpay.CreateRequestUrl(vnp_Url, vnp_HashSecret);
            return urlPayment;
        }
        #endregion
    }
}