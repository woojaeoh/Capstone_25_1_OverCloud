namespace overcloud
{
    public static class DbConfig
    {
        public static string ConnectionString =>
            "server=localhost;port=3306;database=overcloud_new;uid=root;pwd=Wodn134679!!;" +
            "Pooling=true;MinimumPoolSize=5;MaximumPoolSize=50;ConnectionTimeout=30;";
    }
}
