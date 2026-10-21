namespace Function.Infrastructure.Sites;

public static class WordPressConfig
{
    /// <summary>
    /// <c>WORDPRESS_CONFIG_EXTRA</c> of the official image (PHP appended to wp-config.php). <paramref name="homeUrlPhp"/> is
    /// a PHP expression for the site URL. With <paramref name="mysqlTls"/> WordPress connects to MySQL over TLS: Azure
    /// Database for MySQL requires it. The server certificate is not verified, because WordPress cannot hand mysqli a CA
    /// file; the connection is still encrypted.
    /// </summary>
    public static string Extra(string homeUrlPhp, bool mysqlTls)
    {
        var php = $"define('WP_HOME', {homeUrlPhp}); define('WP_SITEURL', WP_HOME);";
        if (mysqlTls) php += " define('MYSQL_CLIENT_FLAGS', MYSQLI_CLIENT_SSL | MYSQLI_CLIENT_SSL_DONT_VERIFY_SERVER_CERT);";
        return php;
    }
}
