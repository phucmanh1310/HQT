using System.ComponentModel.DataAnnotations;

namespace WebBanHangOnline.Models
{
    public class OrderViewModel
    {
        [Required(ErrorMessage = "Họ và tên là bắt buộc")]
        [StringLength(100, ErrorMessage = "Họ và tên không được vượt quá 100 ký tự")]
        public string CustomerName { get; set; }

        [Required(ErrorMessage = "Số điện thoại là bắt buộc")]
        [RegularExpression(@"^\d{10,11}$", ErrorMessage = "Số điện thoại không hợp lệ (10-11 số)")]
        public string Phone { get; set; }

        [Required(ErrorMessage = "Địa chỉ là bắt buộc")]
        [StringLength(500, ErrorMessage = "Địa chỉ không được vượt quá 500 ký tự")]
        public string Address { get; set; }

        [Required(ErrorMessage = "Email là bắt buộc")]
        [EmailAddress(ErrorMessage = "Email không hợp lệ")]
        public string Email { get; set; }

        [Required(ErrorMessage = "Phương thức thanh toán là bắt buộc")]
        public int TypePayment { get; set; }

        public int? TypePaymentVN { get; set; } // Tuỳ chọn cho VNPAY
    }
}