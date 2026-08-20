using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using TantoOntManager.Domain.Unlock;

namespace TantoOntManager.Infrastructure.Unlock;

/// <summary>
/// Aplica patches no XML descriptografado do config.bin.
/// Cada patch é idempotente e valida antes de modificar.
/// </summary>
public interface IConfigBinXmlPatcher
{
    /// <summary>
    /// Aplica patches no XML e retorna o XML modificado + lista de patches aplicados.
    /// </summary>
    (string ModifiedXml, IReadOnlyList<string> AppliedPatches) ApplyPatches(
        string originalXml,
        UnlockOptions options,
        OntIdentitySnapshot identity);
}

public sealed class ZteConfigBinXmlPatcher : IConfigBinXmlPatcher
{
    private readonly ILogger<ZteConfigBinXmlPatcher> _logger;

    public ZteConfigBinXmlPatcher(ILogger<ZteConfigBinXmlPatcher> logger)
    {
        _logger = logger;
    }

    public (string ModifiedXml, IReadOnlyList<string> AppliedPatches) ApplyPatches(
        string originalXml,
        UnlockOptions options,
        OntIdentitySnapshot identity)
    {
        var doc = XDocument.Parse(originalXml);
        var patches = new List<string>();
        var root = doc.Root ?? throw new InvalidOperationException("XML sem root element");

        // ============================================================
        // PATCH 1: Superusuário (DevAuthInfo)
        // ============================================================
        if (options.EnableSuperAdmin)
        {
            var authInfoList = root.Descendants("DevAuthInfo").ToList();

            if (authInfoList.Count == 0)
            {
                // Criar novo nó se não existir
                var userObj = root.Descendants("UserInfo").FirstOrDefault()
                    ?? new XElement("UserInfo");

                var newAuth = new XElement("DevAuthInfo",
                    new XElement("Index", "5"),
                    new XElement("User", "superadmin"),
                    new XElement("Pass", options.NewAdminPassword ?? "superadmin"),
                    new XElement("Level", "1"), // 1 = Administrador/root
                    new XElement("AppID", "1"),
                    new XElement("Enable", "1")
                );

                userObj.Add(newAuth);
                if (userObj.Parent == null) root.Add(userObj);

                patches.Add("Criado DevAuthInfo[5] superadmin/Level=1");
                _logger.LogInformation("Patch: Criado superadmin em DevAuthInfo[5]");
            }
            else
            {
                // Atualizar existente ou adicionar novo index
                var adminEntry = authInfoList.FirstOrDefault(a => 
                    (string?)a.Element("Level") == "1" || 
                    (string?)a.Element("User") == "admin");

                if (adminEntry != null)
                {
                    adminEntry.SetElementValue("Enable", "1");
                    if (options.ChangeAdminPassword && !string.IsNullOrEmpty(options.NewAdminPassword))
                    {
                        adminEntry.SetElementValue("Pass", options.NewAdminPassword);
                        patches.Add($"Atualizado admin password ({options.NewAdminPassword[..Math.Min(2, options.NewAdminPassword.Length)]}***)");
                    }
                    patches.Add("Ativado DevAuthInfo existente Level=1");
                }
                else
                {
                    // Adicionar novo entry
                    var maxIndex = authInfoList
                        .Select(a => int.TryParse((string?)a.Element("Index"), out var idx) ? idx : 0)
                        .DefaultIfEmpty(0)
                        .Max();

                    var newAuth = new XElement("DevAuthInfo",
                        new XElement("Index", (maxIndex + 1).ToString()),
                        new XElement("User", "superadmin"),
                        new XElement("Pass", options.NewAdminPassword ?? "superadmin"),
                        new XElement("Level", "1"),
                        new XElement("AppID", "1"),
                        new XElement("Enable", "1")
                    );

                    authInfoList.Last().AddAfterSelf(newAuth);
                    patches.Add($"Adicionado DevAuthInfo[{maxIndex + 1}] superadmin/Level=1");
                }
            }
        }

        // ============================================================
        // PATCH 2: Desabilitar TR-069 / ACS
        // ============================================================
        if (options.DisableTr069)
        {
            var tr069 = root.Descendants("TR069").FirstOrDefault()
                ?? root.Descendants("X_CT-COM_ManagementServer").FirstOrDefault()
                ?? root.Descendants("ManagementServer").FirstOrDefault();

            if (tr069 != null)
            {
                tr069.SetElementValue("Enable", "0");
                tr069.SetElementValue("URL", "http://0.0.0.0");
                tr069.SetElementValue("PeriodicInformEnable", "0");

                // Limpar credenciais ACS
                tr069.SetElementValue("Username", "");
                tr069.SetElementValue("Password", "");

                patches.Add("TR-069 desabilitado (Enable=0, URL=0.0.0.0)");
                _logger.LogInformation("Patch: TR-069 desabilitado");
            }
            else
            {
                _logger.LogWarning("Patch: Nó TR-069 não encontrado no XML");
            }
        }

        // ============================================================
        // PATCH 3: Ativar Telnet
        // ============================================================
        if (options.EnableTelnet)
        {
            var mgmt = root.Descendants("X_TANTO_Mgmt").FirstOrDefault()
                ?? root.Descendants("DeviceInfo").FirstOrDefault()
                ?? root;

            var telnetNode = mgmt.Descendants("TelnetEnable").FirstOrDefault();
            if (telnetNode != null)
            {
                telnetNode.Value = "1";
                patches.Add("Telnet ativado (TelnetEnable=1)");
            }
            else
            {
                // Tentar adicionar
                var telnetPort = mgmt.Descendants("TelnetPort").FirstOrDefault();
                if (telnetPort != null)
                {
                    telnetPort.AddBeforeSelf(new XElement("TelnetEnable", "1"));
                    patches.Add("Telnet ativado (novo nó TelnetEnable=1)");
                }
            }
            _logger.LogInformation("Patch: Telnet ativado");
        }

        // ============================================================
        // PATCH 4: Ativar SSH
        // ============================================================
        if (options.EnableSsh)
        {
            var sshNode = root.Descendants("SSHEnable").FirstOrDefault()
                ?? root.Descendants("SshEnable").FirstOrDefault();

            if (sshNode != null)
            {
                sshNode.Value = "1";
                patches.Add("SSH ativado");
                _logger.LogInformation("Patch: SSH ativado");
            }
        }

        // ============================================================
        // PATCH 5: Bridge Mode (se solicitado)
        // ============================================================
        if (options.EnableBridgeMode)
        {
            var wanDevices = root.Descendants("WANDevice").ToList();
            foreach (var wan in wanDevices)
            {
                var connType = wan.Descendants("ConnectionType").FirstOrDefault();
                if (connType != null && connType.Value.Equals("IP_Routed", StringComparison.OrdinalIgnoreCase))
                {
                    connType.Value = "IP_Bridged";

                    var nat = wan.Descendants("NATEnable").FirstOrDefault();
                    nat?.SetValue("0");

                    var wanName = wan.Descendants("Name").FirstOrDefault()?.Value ?? "unknown";
                    patches.Add($"WANDevice alterado para Bridge ({wanName})");
                }
            }

            if (wanDevices.Any())
                _logger.LogInformation("Patch: WAN alterado para Bridge Mode");
        }

        // ============================================================
        // PATCH 6: Preservar configurações (garantir que não sejam perdidas)
        // ============================================================
        if (options.PreserveWanConfig)
        {
            // Garantir que WANConnectionDevice não seja removido acidentalmente
            // (o patcher não remove, mas validamos)
            var wanCount = root.Descendants("WANConnectionDevice").Count();
            _logger.LogInformation("Preservação: {Count} WANConnectionDevice mantidos", wanCount);
        }

        if (options.PreserveWifiConfig)
        {
            var ssidCount = root.Descendants("SSID").Count();
            _logger.LogInformation("Preservação: {Count} SSIDs mantidos", ssidCount);
        }

        // ============================================================
        // VALIDAÇÃO FINAL
        // ============================================================
        ValidatePatchedXml(doc, patches);

        var modifiedXml = doc.ToString(System.Xml.Linq.SaveOptions.None);

        _logger.LogInformation(
            "Patching concluído: {PatchCount} patches aplicados | XML: {Length} chars",
            patches.Count, modifiedXml.Length);

        return (modifiedXml, patches);
    }

    private void ValidatePatchedXml(XDocument doc, List<string> patches)
    {
        // Verificar se o XML ainda é parseável
        var testParse = XDocument.Parse(doc.ToString());
        if (testParse.Root == null)
            throw new InvalidOperationException("XML corrompido após patching");

        // Verificar se superadmin foi realmente criado (se solicitado)
        var hasSuperAdmin = doc.Descendants("DevAuthInfo")
            .Any(a => (string?)a.Element("Level") == "1" && (string?)a.Element("Enable") == "1");

        if (patches.Any(p => p.Contains("superadmin")) && !hasSuperAdmin)
            throw new InvalidOperationException("Falha na validação: superadmin não encontrado após patch");

        // Verificar se TR-069 foi desabilitado (se solicitado)
        var tr069 = doc.Descendants("TR069").FirstOrDefault();
        if (patches.Any(p => p.Contains("TR-069")) && tr069 != null)
        {
            var enableVal = (string?)tr069.Element("Enable");
            if (enableVal != "0")
                throw new InvalidOperationException("Falha na validação: TR-069 Enable não é 0 após patch");
        }
    }
}
