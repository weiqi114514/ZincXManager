#include "ZFile.h"
#include "src/logging/logger.h"      // 使用 logging::log
#include <vector>
#include <filesystem>
#include <iterator>
#include <algorithm>
#include <iostream>

using namespace logging;

namespace zFile
{
    ZFile logF;          // 全局对象定义

    bool ZFile::fileOperate(const std::string& fileName, const std::string& mode)
    {
        if (mode == "rwNfC")//如果文件存在就打开，不存在则创建并开启
        {
            if (zF.is_open()) zF.close();
            zF.clear();
            zF.open(fileName, std::ios::in | std::ios::out);
            if (!zF.is_open())
            {
                zF.clear();
                zF.open(fileName, std::ios::out);
                zF.close();
                zF.clear();
                zF.open(fileName, std::ios::in | std::ios::out);
            }
            if (zF.is_open())
                currentFile = fileName;
            return zF.is_open();
        }
        else if (mode == "del")//删除文件操作
        {
            if (zF.is_open()) zF.close();
            std::error_code ec;
            bool ok = std::filesystem::remove(fileName, ec);   // 注意：用 fileName 而不是 "fileName"
            if (!ok)
                logging::log(LogL::Error, "[zFile]del:删除文件失败，错误信息：" + ec.message());
            return ok;
        }
        else if (mode == "close")
        {
            if (!zF.is_open())
            {
                logging::log(LogL::Warning, "[zFile]close:关闭文件为无效操作，文件已经关闭");
                return false;
            }
            zF.close();
            currentFile.clear();
            return true;
        }
        else if (mode == "replaceFile")//替换开启文件操作
        {
            if (zF.is_open()) zF.close();
            zF.clear();
            zF.open(fileName, std::ios::in | std::ios::out);
            if (!zF.is_open())
            {
                zF.clear();
                zF.open(fileName, std::ios::out);
                zF.close();
                zF.clear();
                zF.open(fileName, std::ios::in | std::ios::out);
            }
            if (zF.is_open())
                currentFile = fileName;
            return zF.is_open();
        }
        else
        {
            logging::log(LogL::Warning, "[zFile]未知操作模式：" + mode);
            return false;
        }
    }

    std::string ZFile::readFileTxt()//读取txt文件
    {
        if (!zF.is_open())
        {
            logging::log(LogL::Error, "[zFile]readFileTxt:读取TXT文件为无效操作，文件未开启");
            return "";
        }
        zF.clear();
        zF.seekg(0, std::ios::beg);
        return std::string((std::istreambuf_iterator<char>(zF)),
            std::istreambuf_iterator<char>());
    }

    std::string ZFile::readFileBin()//读取二进制文件
    {
        if (currentFile.empty())
        {
            logging::log(LogL::Error, "[zFile]readFileBin：当前没有打开的文件");
            return "";
        }
        if (zF.is_open()) zF.close();
        zF.clear();
        zF.open(currentFile, std::ios::in | std::ios::binary);
        if (!zF.is_open())
        {
            logging::log(LogL::Error, "[zFile]readFileBin：打开失败 " + currentFile);
            return "";
        }
        std::string data((std::istreambuf_iterator<char>(zF)),
            std::istreambuf_iterator<char>());
        zF.close();
        zF.open(currentFile, std::ios::in | std::ios::out);
        return data;
    }

    std::string ZFile::readDir()
    {
        // 原设计无参，无法确定目录。建议后续改为 readDir(const std::string& dirPath)
        logging::log(LogL::Warning, "[zFile]readDir:未指定目录，请使用带参数的版本");
        return "";
    }

    bool ZFile::writeFileTxt(std::string in, bool endl)//追加写入，endl决定换行
    {
        if (!zF.is_open())
        {
            //logging::log(LogL::Error, "[zFile]writeFileTxt：文件未打开");
            return false;
        }
        zF.clear();
        zF.seekp(0, std::ios::end);
        zF << in;
        if (endl) zF << "\n";
        zF.flush();
        return static_cast<bool>(zF);
    }

    bool ZFile::writeFileTxt(std::string in, bool endl, int line)//写入文件，从输入行开始
    {
        if (!zF.is_open())
        {
            std::cerr << "[zFile]writeFileTxt:文件未打开";
            return false;
        }
        if (line < 1)
        {
            logging::log(LogL::Warning, "[zFile]writeFileTxt:行号必须 >= 1");
            return false;
        }
        zF.clear();
        zF.seekg(0, std::ios::beg);
        std::string all((std::istreambuf_iterator<char>(zF)),
            std::istreambuf_iterator<char>());

        std::vector<std::string> lines;
        std::string cur;
        for (char c : all)
        {
            if (c == '\n') { lines.push_back(cur); cur.clear(); }
            else            cur += c;
        }
        if (!cur.empty()) lines.push_back(cur);

        std::string out;
        int keep = std::min<int>(line - 1, static_cast<int>(lines.size()));
        for (int i = 0; i < keep; ++i)
            out += lines[i] + "\n";
        out += in;
        if (endl) out += "\n";

        zF.clear();
        zF.seekp(0, std::ios::beg);
        zF << out;
        zF.flush();
        return static_cast<bool>(zF);
    }

    bool ZFile::mapEdit(std::string key, std::string value)//键值追加/修改
    {
        if (!zF.is_open())
        {
            logging::log(LogL::Error, "[zFile]mapEdit:文件未打开");
            return false;
        }
        if (key.empty())
        {
            logging::log(LogL::Warning, "[zFile]mapEdit:key 不能为空");
            return false;
        }

        zF.clear();
        zF.seekg(0, std::ios::beg);
        std::string all((std::istreambuf_iterator<char>(zF)),
            std::istreambuf_iterator<char>());

        std::vector<std::string> lines;
        std::string cur;
        for (char c : all)
        {
            if (c == '\n') { lines.push_back(cur); cur.clear(); }
            else            cur += c;
        }
        if (!cur.empty()) lines.push_back(cur);

        std::string target = key + "=";
        bool found = false;
        for (auto& line : lines)
        {
            if (line.rfind(target, 0) == 0)
            {
                line = key + "=" + value;
                found = true;
                break;
            }
        }
        if (!found)
            lines.push_back(key + "=" + value);

        std::string out;
        for (auto& line : lines) out += line + "\n";

        zF.close();
        zF.open(currentFile, std::ios::out | std::ios::trunc);
        zF << out;
        zF.flush();
        zF.close();
        zF.open(currentFile, std::ios::in | std::ios::out);
        return true;
    }
}