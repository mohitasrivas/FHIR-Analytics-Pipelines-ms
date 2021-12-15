# FHIR to Synapse Sync Agent

FHIR to Synapse Sync Agent enables your to perform Analytics and Machine Learning on FHIR data by moving FHIR data to Azure Data Lake in near real time and making it available to a Synapse workspace.

It is an Azure Function that extracts data from a FHIR server using FHIR Resource APIs, converts it to hierarchial Parquet files, and writes it to Azure Data Lake in near real time. This solution also contains a script to create External Tables and Views in Synapse Serverless SQL pool pointing to the Parquet files.

This solution enables you to query against the entire FHIR data with tools such as Synapse Studio, SSMS, and Power BI. You can also access the Parquet files directly from a Synapse Spark pool. You should consider this solution if you want to access all of your FHIR data in near real time, and want to defer custom transformation to downstream systems.

## Deployment

### Prerequsites

1. An instance of Azure API for FHIR, FHIR Server for Azure, or the FHIR server in Azure Healthcare APIs. The pipeline will sync data from this FHIR server.
1. A Synapse workspace.

### Steps at high level

1. Deploy the pipeline using the given ARM template.
1. Provide access of the FHIR server to the Azure Function that gets deployed in the first step.
1. Verify that data is getting copied to the Storage Account. If so, the pipeline is working successfully.
1. Provide access of the Storage Account and the Synapse workspace to your account for running the PowerScript mentioned below.
1. Provide access of the Storage Account to the Synapse Workspace in order to access data from Synapse.
1. Run the provided PowerShell script that creates following artifacts:
    1. Resource specific folders in the Azure Storage Account.
    1. A database in Synapse serverless pool with External Tables and Views pointing to the files in the Storage Account.
1. Query data from Synapse Studio

### 1. Deploy the pipeline

1. Go to [Custom deployment](https://ms.portal.Azure.com/#create/Microsoft.Template) on Azure and select **Build your own template in the editor**.
2. Copy the content of the provided [ARM template](../deploy/templates/FhirSynapsePipelineTemplate.json) to the edit box and click **Save**. It should open the following form.

![image](./assets/templateParameters.png)

3. Fill the form based on the table below and click on **Review and Create** to start the deployment.

|Parameter   | Description   |
|---|---|
| Resource Group | Name of the resource group where you want the pipeline related resources to be created. |
| App Name  | A name for the Azure Function.  |
| Fhir Server Url  | The URL of the FHIR server |
| Authentication  |  Whether to access the FHIR server with managed identity authentication. Set false if you are using an instance of FHIR server for Azure with public access |
| Fhir version | Version of the FHIR server. Currently only R4 is supported |
| Data Start | Start time stamp of the data to be exported. |
| Data End | Start time stamp of the data to be exported. Leave it empty if you want to periodically export data in real time.  |
| Container name | A name for the Storage Account container to which Parquet files will be written. The Storage Account with autogenerated name will automatically be created during the installation |
| Package url | The build package of the agent. You need not change this |
| App Insight Location | You can find logs in the deployed application insight resource. You need not change this |

4. Note the names of _Storage Account_ the _Azure Function App_ created during the deployment.

### 2. Provide Access of the FHIR server to the Azure Function.

If you are using the Azure API for FHIR, or the FHIR server in Azure Healthcare APIs then provide **FHIR Data Reader** role to the Azure Function noted above.

If you are using FHIR Server for Azure with anonymous access then you can skip this step.

### 3. Verify data movement

The Azure Function app deployed earlier runs automatically. You can see the progress of the Azure Function from Azure portal. The time taken to write the data to the storage account depends on the amount of data in the FHIR server. Once the Function execution is completed, you should be able to see Parquet files in the Storage Account. Go to the _results_ folder inside the container and you should see folders corresponding to different FHIR resources. Note that you will see folders for only those Resources that are present in your FHIR server. Running the PowerShell script in a later step will create folders for other Resources.

![blob result](./assets/ExportedData.png)

### 4. Provide privilege to your account

You need to provide following roles to your account in order to run the PowerShell script in the next step. You may revoke these roles after the installation is complete.

1. In your Synapse workspace, go to **Synapse Studio -> Manage -> Access Control** and provide _Synapse Administrator_ role to your account.
1. In the Storage Account created during the pipeline installation, go to **Access Control (IAM)** and provide _Storage Blob Data Contributor_ role to your account.

### 5. Provide access of the Storage Account to the Synapse Workspace

To enable Synapse to read the data from the Storage Account, provide _Storage Blob Data Contributor_ role to it. You can do so by selecting **Managed identify** while adding members to the role. You should be able to pick your Synapse workspace instance from the list of managed identities shown on the portal.

### 6. Run the PowerShell script

Running the PowerShell script that creates following artifacts:

1. Resource specific folders in the Azure Storage Account.
1. A database in Synapse serverless pool with External Tables and Views pointing to the files in the Storage Account.

In order to run the PowerShell Script, use following steps:

1. Ensure that you have the latest version of the PowerShell.
1. Clone this repo to your local machine.
1. Open Azure CLI and go to scripts folder.
1. Login to your Azure account using `Connect-AzAccount -SubscriptionId 'yyyy-yyyy-yyyy-yyyy'`. Use your subscription id.
1. Run `./Set-SynapseEnvironment.ps1 -SqlServerEndpoint "{Your Synapse serverless SQL pool endpoint}" -Storage "{Your storage account where Parquet files are written}"`. Refer to the complete syntax below.

``` PowerShell
Set-SynapseEnvironment
  [-SqlServerEndpoint] <string>
  [-Storage] <string>
  [[-Database] <string>, default: “fhirdb”]
  [[-Container] <string>, default: “fhir”]
  [[-ResultPath] <string>, default: “result”]
  [[-MasterKey] <string>, default: ”FhirSynapseLink0!”]
  [[-Concurrent] <int>, default: 25]
```

|Parameter   | Description   |
|---|---|
| SqlServerEndpoint | Synapse serverless SQL pool endpoint. E.g. example-ondemand.sql.azuresynapse.net |
| Storage | Name of Storage Account where parquet files are stored. |
| Database | Name of database to be created on Synapse serverless SQL pool |
| Container | Name of container on storage where parquet files are stored. |
| ResultPath | Path to the parquet folder. |
| MasterKey | Master key that will be set in created database. Database need to have master key then we can create EXTERNAL TABLEs and VIEWs on it. |
| Concurrent | Max concurrent tasks number that will be used to upload place holder files and execute SQL scripts. |

### 7. Query data from Synapse Studio

Go to your Synapse workspace. You should see a new database named _fhirdb_. Expand _External Tables_ and _Views_ to see the entities. Your FHIR data is now ready to be queried.

As you add more data to the FHIR server, it will be fetched automatically to the Data Lake and become available for querying.