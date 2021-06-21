namespace Hkmp.Api {
    public interface INetClient {
        
        /**
         * Whether the client is currently connected to a server
         */
        bool IsConnected { get; }
        
    }
}