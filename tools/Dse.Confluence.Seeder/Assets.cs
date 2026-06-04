// Copyright (c) PNC Financial Services. All rights reserved.


using System.Text;

namespace Dse.Confluence.Seeder;

// In-process binaries uploaded as attachments so ri:attachment / ac:image references resolve.
public static class Assets
{
    public const string SampleManifestYaml = """
                                             manifest:
                                               group: pnc.abc.ocp4.deployments
                                               artifactId: abc-secrets-deploy-cyberark-manifest
                                               version: 7
                                               components:
                                                 - name: pnc.ddp.samples.openshift-secret
                                                   type: openshift-secret
                                                   vars:
                                                     source: cyberark
                                                     openshift_secret_name: pnc.ddp.samples
                                                   environments:
                                                     prod:
                                                       vars:
                                                         oc_project: ddp-prod
                                                         openshift_secret_entries:
                                                           - entry_key: ddp_password
                                                             entry_value:
                                                               cyberark_safe: ABC-DYNAMIC-PROD
                                                               cyberark_address: prod.pncint.net
                                                               cyberark_username: demo_username
                                             """;

    public const string SampleJenkinsfile = """
                                            stage('Deploy Secrets') {
                                              when { branch pattern: "^(stage|release)-.*", comparator: "REGEXP" }
                                              steps {
                                                script {
                                                  cirSecrets = 'SER/CIR/Stage_Promote_Release_cir/cir-secrets-deploy/' + env.GIT_BRANCH
                                                  build job: cirSecrets, wait: true, parameters: [[$class: 'StringParameterValue', name: 'CR', value: env.CR]]
                                                }
                                              }
                                            }
                                            """;

    // Valid 1x1 transparent PNG.
    public static byte[] Png { get; } = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mNk+M9QDwAEhgGAhKmMIQAAAABJRU5ErkJggg==");

    public static string SampleCertB64 { get; } = Convert.ToBase64String(
        Encoding.UTF8.GetBytes(
            "-----BEGIN CERTIFICATE-----\nMIIB...sample-non-secret-placeholder...AB\n-----END CERTIFICATE-----\n"));

    public static byte[] Text(string content) => Encoding.UTF8.GetBytes(content);
}
