namespace MoyuBox
{
    public static class NavigationMessages
    {
        public static string Failure(string code,int status)
        {
            if(code.StartsWith("Certificate")||code=="ServerCertificateError")return "网站证书校验失败（"+code+"）。请检查系统日期或联系网站维护者；可在默认浏览器中核实。";
            if(status>=400)return "网站返回 HTTP "+status+"。请稍后重试，或在默认浏览器中核实。";
            if(code=="HostNameNotResolved")return "无法解析网站地址，请检查网址与网络连接。";
            if(code=="Timeout")return "等待网站响应超时，请重试或在默认浏览器中核实。";
            return "网页连接失败（"+code+"）。请检查网络，或在默认浏览器中核实。";
        }
    }
}
