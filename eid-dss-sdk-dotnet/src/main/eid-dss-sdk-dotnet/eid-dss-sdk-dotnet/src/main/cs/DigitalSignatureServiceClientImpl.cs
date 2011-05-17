﻿using System;
using System.Collections.Generic;
using System.Text;

using DSSWSNamespace;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.ServiceModel;
using System.Net;
using System.Xml;
using System.Xml.Serialization;
using System.IO;



namespace eid_dss_sdk_dotnet
{
    public class DigitalSignatureServiceClientImpl : DigitalSignatureServiceClient
    {
        private String location;

        private bool logging = true;

        private DigitalSignatureServicePortTypeClient client;

        private X509Certificate sslCertificate;

        public DigitalSignatureServiceClientImpl(String location)
        {
            this.location = location;
        }

        public void setLogging(bool logging)
        {
            this.logging = logging;
        }

        private void setupClient()
        {
            EndpointAddress remoteAddress = new EndpointAddress(this.location);

            bool sslLocation = this.location.StartsWith("https") ? true : false;
            if (sslLocation)
            {
                if (null != this.sslCertificate)
                {
                    /*
                     * Setup SSL validation
                     */
                    Console.WriteLine("SSL Validation active");
                    ServicePointManager.ServerCertificateValidationCallback =
                        new RemoteCertificateValidationCallback(CertificateValidationCallback);
                }
                else
                {
                    Console.WriteLine("Accept ANY SSL Certificate");
                    ServicePointManager.ServerCertificateValidationCallback =
                        new RemoteCertificateValidationCallback(WCFUtil.AnyCertificateValidationCallback);
                }
            }

            if (null == this.client)
            {
                // Setup basic client
                if (sslLocation)
                {
                    this.client = new DigitalSignatureServicePortTypeClient(
                        WCFUtil.BasicHttpOverSSLBinding(), remoteAddress);
                }
                else
                {
                    this.client = new DigitalSignatureServicePortTypeClient(
                        new BasicHttpBinding(), remoteAddress);
                }

                // Add logging behaviour
                if (this.logging)
                {
                    this.client.Endpoint.Behaviors.Add(new LoggingBehavior());
                }
            }
        }

        public void configureSsl(X509Certificate2 sslCertificate)
        {
            this.sslCertificate = sslCertificate;
        }

        private bool CertificateValidationCallback(Object sender, X509Certificate certificate,
            X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            Console.WriteLine("Certificate Validation Callback");
            bool result = certificate.Equals(this.sslCertificate);
            Console.WriteLine("TLS Authn Result: " + result);
            return result;
        }

        public bool verify(byte[] signedDocument, string mimeType)
        {
            ResponseBaseType response = doVerification(signedDocument, mimeType, false, false);

            String resultminor = validateResult(response);
            if (null == resultminor)
            {
                throw new SystemException("Missing ResultMinor");
            }

            if (DSSConstants.RESULT_MINOR_VALID_SIGNATURE.Equals(resultminor) ||
                DSSConstants.RESULT_MINOR_VALID_MULTI_SIGNATURES.Equals(resultminor))
            {
                return true;
            }

            return false;
        }

        public List<SignatureInfo> verifyWithSigners(byte[] signedDocument, String mimeType)
        {
            ResponseBaseType response = doVerification(signedDocument, mimeType, false, true);

            validateResult(response);

            // TODO: parse verificationReport
            List<SignatureInfo> signers = new List<SignatureInfo>();
            DSSXSDNamespace.VerificationReportType verificationReport = findVerificationReport(response);
            if (null == verificationReport)
            {
                return signers;
            }

            foreach (DSSXSDNamespace.IndividualReportType individualReport in verificationReport.IndividualReport)
            {

                if (!DSSConstants.RESULT_MAJOR_SUCCESS.Equals(individualReport.Result.ResultMajor))
                {
                    Console.WriteLine("WARNING: invalid VR result reported: " +
                        individualReport.Result.ResultMajor);
                    continue;
                }

                DSSXSDNamespace.SignedObjectIdentifierType signedObjectIdentifier
                    = individualReport.SignedObjectIdentifier;

                DateTime signingTime = signedObjectIdentifier.SignedProperties
                    .SignedSignatureProperties.SigningTime;
                X509Certificate signer = null;
                String role = null;

                foreach (XmlElement detail in individualReport.Details.Any)
                {
                    if (detail.NamespaceURI.Equals(DSSConstants.VR_NAMESPACE) &&
                        detail.LocalName.Equals("DetailedSignatureReport"))
                    {
                        DSSXSDNamespace.DetailedSignatureReportType detailedSignatureReport =
                            (DSSXSDNamespace.DetailedSignatureReportType)fromDom("DetailedSignatureReport",
                            DSSConstants.VR_NAMESPACE, detail,
                            typeof(DSSXSDNamespace.DetailedSignatureReportType));

                        DSSXSDNamespace.CertificateValidityType certificateValidity =
                            detailedSignatureReport.CertificatePathValidity
                                .PathValidityDetail.CertificateValidity[0];

                        byte[] encodedSigner = certificateValidity.CertificateValue;
                        signer = new X509Certificate(encodedSigner);

                        if (null != detailedSignatureReport.Properties)
                        {
                            DSSXSDNamespace.SignerRoleType1 signerRole = detailedSignatureReport.Properties
                                .SignedProperties.SignedSignatureProperties.SignerRole;
                            if (null != signerRole)
                            {
                                role = signerRole.ClaimedRoles[0].Any[0].Value;
                            }
                        }
                    }
                }

                if (null == signer)
                {
                    throw new SystemException("No signer certificate present in verification report.");
                }

                signers.Add(new SignatureInfo(signer, signingTime, role));
            }
            return signers;
        }

        private DSSXSDNamespace.VerificationReportType findVerificationReport(ResponseBaseType responseBase)
        {
            if (null == responseBase.OptionalOutputs)
            {
                return null;
            }
            foreach (XmlElement optionalOutput in responseBase.OptionalOutputs.Any)
            {
                if (optionalOutput.NamespaceURI.Equals(DSSConstants.VR_NAMESPACE) &&
                    optionalOutput.LocalName.Equals("VerificationReport"))
                {
                    DSSXSDNamespace.VerificationReportType verificationReport =
                        (DSSXSDNamespace.VerificationReportType)fromDom("VerificationReport",
                        DSSConstants.VR_NAMESPACE, optionalOutput,
                        typeof(DSSXSDNamespace.VerificationReportType));
                    return verificationReport;
                }
            }

            return null;
        }

        private ResponseBaseType doVerification(byte[] documentData, String mimeType,
            bool returnSignerIdentity, bool returnVerificationReport)
        {

            Console.WriteLine("Verify");
            // setup the client
            setupClient();

            String requestId = "dss-verify-request-" + Guid.NewGuid().ToString();
            VerifyRequest verifyRequest = new VerifyRequest();
            verifyRequest.RequestID = requestId;

            AnyType optionalInputs = new AnyType();
            List<XmlElement> optionalInputElements = new List<XmlElement>();
            if (returnSignerIdentity)
            {
                XmlElement e = getElement("dss", "ReturnSignerIdentity", DSSConstants.DSS_NAMESPACE);
                optionalInputElements.Add(e);
            }

            if (returnVerificationReport)
            {
                DSSXSDNamespace.ReturnVerificationReport returnVerificationReportElement =
                    new DSSXSDNamespace.ReturnVerificationReport();
                returnVerificationReportElement.IncludeVerifier = false;
                returnVerificationReportElement.IncludeCertificateValues = true;
                returnVerificationReportElement.ReportDetailLevel =
                    "urn:oasis:names:tc:dss-x:1.0:profiles:verificationreport:reportdetail:noDetails";

                XmlElement e = toDom("ReturnVerificationReport", DSSConstants.VR_NAMESPACE,
                    returnVerificationReportElement, typeof(DSSXSDNamespace.ReturnVerificationReport));

                optionalInputElements.Add(e);
            }

            if (optionalInputElements.Count > 0)
            {
                optionalInputs.Any = optionalInputElements.ToArray();
                verifyRequest.OptionalInputs = optionalInputs;
            }

            verifyRequest.InputDocuments = getInputDocuments(documentData, mimeType);

            // operate
            ResponseBaseType response = this.client.verify(verifyRequest);

            // check response
            checkResponse(response, verifyRequest.RequestID);

            return response;
        }

        private InputDocuments getInputDocuments(byte[] documentData, String mimeType)
        {
            InputDocuments inputDocuments = new InputDocuments();

            DocumentType document = new DocumentType();
            if (null == mimeType || mimeType.Equals("text/xml"))
            {
                document.Item = documentData;
            }
            else
            {
                Base64Data base64Data = new Base64Data();
                base64Data.MimeType = mimeType;
                base64Data.Value = documentData;
                document.Item = base64Data;
            }

            inputDocuments.Items = new object[] { document };
            return inputDocuments;
        }

        private void checkResponse(ResponseBaseType response, String requestId)
        {
            if (null == response)
            {
                throw new SystemException("No response returned");
            }
            String responseRequestId = response.RequestID;
            if (null == responseRequestId)
            {
                throw new SystemException("Missing Response.RequestID");
            }
            if (!responseRequestId.Equals(requestId))
            {
                throw new SystemException("Incorrect Response.RequestID");
            }
        }

        private String validateResult(ResponseBaseType response)
        {
            Result result = response.Result;
            String resultMajor = result.ResultMajor;
            String resultMinor = result.ResultMinor;
            Console.WriteLine("result major: " + resultMajor);
            if (!DSSConstants.RESULT_MAJOR_SUCCESS.Equals(resultMajor))
            {
                Console.WriteLine("result minor: " + resultMinor);
                if (null != resultMinor && resultMinor.Equals(
                    DSSConstants.RESULT_MINOR_NOT_PARSEABLE_XML_DOCUMENT))
                {
                    throw new NotParseableXMLDocumentException();
                }
                throw new SystemException("unsuccessful result: " + resultMajor);
            }
            return resultMinor;
        }

        private XmlElement getElement(String prefix, String elementName, String ns)
        {
            XmlDocument xmlDocument = new XmlDocument();
            return xmlDocument.CreateElement(prefix, elementName, ns);
        }

        private XmlElement toDom(String elementName, String ns, Object o, Type type)
        {
            // serialize to DOM
            XmlSerializerNamespaces namespaces = new XmlSerializerNamespaces();
            namespaces.Add("dss", DSSConstants.DSS_NAMESPACE);
            namespaces.Add("vr", DSSConstants.VR_NAMESPACE);
            namespaces.Add("artifact", DSSConstants.ARTIFACT_NAMESPACE);

            XmlRootAttribute xRoot = new XmlRootAttribute();
            xRoot.ElementName = elementName;
            xRoot.Namespace = ns;
            XmlSerializer serializer = new XmlSerializer(type, xRoot);
            MemoryStream memoryStream = new MemoryStream();
            XmlTextWriter xmlTextWriter = new XmlTextWriter(memoryStream, Encoding.UTF8);
            serializer.Serialize(xmlTextWriter, o, namespaces);

            XmlDocument xmlDocument = new XmlDocument();
            memoryStream.Seek(0, SeekOrigin.Begin);
            xmlDocument.Load(memoryStream);

            return (XmlElement)xmlDocument.ChildNodes.Item(1);
        }

        private Object fromDom(String elementName, String ns, XmlNode xmlNode, Type type)
        {
            XmlRootAttribute xRoot = new XmlRootAttribute();
            xRoot.ElementName = elementName;
            xRoot.Namespace = ns;

            XmlSerializer serializer = new XmlSerializer(type, xRoot);

            XmlReader xmlReader = new XmlNodeReader(xmlNode);

            return serializer.Deserialize(xmlReader);
        }
    }
}