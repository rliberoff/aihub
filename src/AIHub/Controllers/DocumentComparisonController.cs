using OpenAI.Chat;

namespace MVCWeb.Controllers;

public class DocumentComparisonController : Controller
{
    private string FormRecogEndpoint;
    private string FormRecogSubscriptionKey;
    private string AOAIendpoint;
    private string AOAIsubscriptionKey;
    private string storageconnstring;
    private string AOAIDeploymentName;
    private readonly BlobContainerClient containerClient;
    private readonly IEnumerable<BlobItem> blobs;
    private Uri sasUri;
    private HttpClient httpClient;

    private DocumentComparisonModel model;

    public DocumentComparisonController(IConfiguration config, IHttpClientFactory clientFactory)
    {
        FormRecogEndpoint = config.GetValue<string>("DocumentComparison:FormRecogEndpoint") ?? throw new ArgumentNullException("FormRecogEndpoint");
        FormRecogSubscriptionKey = config.GetValue<string>("DocumentComparison:FormRecogSubscriptionKey") ?? throw new ArgumentNullException("FormRecogSubscriptionKey");
        AOAIendpoint = config.GetValue<string>("DocumentComparison:OpenAIEndpoint") ?? throw new ArgumentNullException("OpenAIEndpoint");
        AOAIsubscriptionKey = config.GetValue<string>("DocumentComparison:OpenAISubscriptionKey") ?? throw new ArgumentNullException("OpenAISubscriptionKey");
        storageconnstring = config.GetValue<string>("Storage:ConnectionString") ?? throw new ArgumentNullException("ConnectionString");
        AOAIDeploymentName = config.GetValue<string>("DocumentComparison:DeploymentName") ?? throw new ArgumentNullException("DeploymentName");
        BlobServiceClient blobServiceClient = new BlobServiceClient(storageconnstring);
        containerClient = blobServiceClient.GetBlobContainerClient(config.GetValue<string>("DocumentComparison:ContainerName"));
        sasUri = containerClient.GenerateSasUri(Azure.Storage.Sas.BlobContainerSasPermissions.Read, DateTimeOffset.UtcNow.AddHours(1));
        // Obtiene una lista de blobs en el contenedor
        blobs = containerClient.GetBlobs();
        model = new DocumentComparisonModel();
        httpClient = clientFactory.CreateClient();
    }

    public IActionResult DocumentComparison()
    {
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> DocumentComparison(string[] document_urls, string[] tab_names, string prompt)
    {
        //1. Get Documents
        model.PdfUrl1 = document_urls[0] + sasUri.Query;
        model.PdfUrl2 = document_urls[1] + sasUri.Query;
        ViewBag.PdfUrl1 = document_urls[0] + sasUri.Query;
        ViewBag.PdfUrl2 = document_urls[1] + sasUri.Query;
        model.tabName1= tab_names[0];
        model.tabName2= tab_names[1];

        string[] output_result = new string[2];

        //2. Call Form Recognizer
        for (int i = 0; i < document_urls.Length; i++)
        {
            var client = new DocumentAnalysisClient(new Uri(FormRecogEndpoint), new AzureKeyCredential(FormRecogSubscriptionKey));
            var operation = await client.AnalyzeDocumentFromUriAsync(WaitUntil.Completed, "prebuilt-layout", new Uri(document_urls[i] + sasUri.Query));
            output_result[i] = operation.Value.Content;
        }

        try
        {
            Uri aoaiEndpointUri = new(AOAIendpoint);

            AzureOpenAIClient azureClient = string.IsNullOrEmpty(AOAIsubscriptionKey)
                ? new(aoaiEndpointUri, new DefaultAzureCredential())
                : new(aoaiEndpointUri, new AzureKeyCredential(AOAIsubscriptionKey));

            ChatClient chatClient = azureClient.GetChatClient(AOAIDeploymentName);

            var messages = new ChatMessage[]
            {
                new SystemChatMessage($@"You are specialized in analyze different versions of the same PDF document. The first Document OCR result is: <<<{output_result[0]}>>> and the second Document OCR result is: <<<{output_result[1]}>>>"),
                new UserChatMessage($@"User question: {prompt}"),
            };

            ChatCompletionOptions chatCompletionOptions = new()
            {
                MaxTokens = 1000,
                Temperature = 0.7f,
                FrequencyPenalty = 0,
                PresencePenalty = 0,
                TopP = 0.95f
            };

            ChatCompletion completion = await chatClient.CompleteChatAsync(messages, chatCompletionOptions);

            model.Message = completion.Content[0].Text;
            ViewBag.Message = completion.Content[0].Text;
        }
        catch (RequestFailedException)
        {
            throw;
        }

        return Ok(model);
    }

    // Upload a file to my azure storage account
    [HttpPost]
    public async Task<IActionResult> UploadFile()
    {

        List<IFormFile> documentFiles = Request.Form.Files.ToList();

        // pre-validations
        if (documentFiles == null || !documentFiles.Count.Equals(2))
        {
            ViewBag.Message = "You must upload exactly two documents";
            return View("DocumentComparison", model);
        }

        foreach (var documentFile in documentFiles)
        {
            if (CheckImageExtension(documentFile.FileName.ToString()))
            {
                ViewBag.Message = "You must upload pdf documents only";
                return View("DocumentComparison", model);
            }
        }

        string[] urls = new string[2];
        string[] tabNames = new string[2];
        for (int i = 0; i < documentFiles.Count; i++)
        {
            string url = documentFiles[i].FileName.ToString();
            url = url.Replace(" ", "");
            tabNames[i] = url;
            BlobClient blobClient = containerClient.GetBlobClient(url);
            var httpHeaders = new BlobHttpHeaders
            {
                ContentType = "application/pdf",
            };
            await blobClient.UploadAsync(documentFiles[i].OpenReadStream(), new BlobUploadOptions { HttpHeaders = httpHeaders });
            urls[i] = blobClient.Uri.ToString();
        }

        // Call EvaluateImage with the url
        await DocumentComparison(urls, tabNames, Request.Form["prompt"].ToString());
        ViewBag.Waiting = null;

        return Ok(model);
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }

    private static bool CheckImageExtension(string filename)
    {
        return !filename.Contains(@".pdf", StringComparison.OrdinalIgnoreCase);
    }
}