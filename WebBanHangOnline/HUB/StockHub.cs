using Microsoft.AspNet.SignalR;
using Microsoft.AspNet.SignalR.Hubs;

namespace WebBanHangOnline.Hubs
{
    [HubName("stockHub")]
    public class StockHub : Hub
    {
        public void SendStockUpdate(int productId, int quantity)
        {
            Clients.All.ReceiveStockUpdate(productId, quantity);
        }
    }
}