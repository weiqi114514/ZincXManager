#include "ZFile.h"
#include "src/logging/logger.h"      // 使用 logging::log
#include <vector>
#include <filesystem>
#include <iterator>
#include <algorithm>
#include <iostream>
#include <sstream>
using namespace logging;

namespace zFile
{
    ZFile logF;          // 全局对象定义

    bool ZFile::fileOperate(const std::string& fileName, const std::string& mode)
    {
        if (mode == "rwNFC")//如果文件存在就打开，不存在则创建并开启
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
            if (zF.is_open())
            {
                logging::log(LogL::Info, "[zFile]开启或并创建文件" + fileName);
            }
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
                logging::log(LogL::Info, "[zFile]开启文件" + fileName);
            return zF.is_open();
        }
        else
        {
            logging::log(LogL::Warning, "[zFile]未知操作模式：" + mode);
            return false;
        }
    }

    bool ZFile::closeFile()
    {
        if (!zF.is_open())
        {
            logging::log(LogL::Warning, "[zFile]close:关闭文件为无效操作，文件已经关闭");
            return false;
        }
        zF.close();
        currentFile.clear();
        logging::log(LogL::Info, "[zFile]文件关闭");
        return true;
    }

   
    std::vector<std::string> ZFile::readFileTxt() // 读取文本文件，按行拆好返回（对应原来的 readFileTxt）
    {
        std::vector<std::string> lines;

        if (!zF.is_open())
        {
            logging::log(LogL::Error, "[zFile]readFileTxt:读取TXT文件为无效操作，文件未开启");
            return lines;   // 空 vector
        }

        // 读到 EOF 后状态位会脏，先清
        zF.clear();
        zF.seekg(0, std::ios::beg);

        std::string line;
        while (std::getline(zF, line))
        {
            // 去掉行尾 \r（Windows 换行）
            if (!line.empty() && line.back() == '\r')
                line.pop_back();

            lines.push_back(line);
        }

        zF.clear();   // 读完清掉 eofbit，方便后续操作
        return lines;
    }

    std::map<std::string, std::string> ZFile::readFileMap()
    {
        std::map<std::string, std::string> result;

        if (!zF.is_open())
        {
            logging::log(LogL::Error, "[zFile]readFileMap:文件未打开");
            return result;
        }

        // 读到 EOF 后状态位会脏，先清
        zF.clear();
        zF.seekg(0, std::ios::beg);

        std::string line;
        while (std::getline(zF, line))
        {
            // 去掉行尾 \r（Windows 换行）
            if (!line.empty() && line.back() == '\r')
                line.pop_back();

            // 跳过空行和注释（# 或 ; 开头）
            if (line.empty()) continue;
            if (line[0] == '#' || line[0] == ';') continue;

            // 找第一个 '='
            auto pos = line.find('=');
            if (pos == std::string::npos) continue;   // 没有等号，跳过

            // 切 key / value 并去首尾空格
            std::string key = line.substr(0, pos);
            std::string value = line.substr(pos + 1);

            auto trim = [](std::string& s)
                {
                    const char* ws = " \t\r\n";
                    auto b = s.find_first_not_of(ws);
                    auto e = s.find_last_not_of(ws);
                    if (b == std::string::npos) { s.clear(); return; }
                    s = s.substr(b, e - b + 1);
                };
            trim(key);
            trim(value);

            if (!key.empty())
                result[key] = value;
        }

        zF.clear();   // 读完清掉 eofbit，方便后续操作
        return result;
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

    
// 目录项后缀 '/' 表示子目录，其余为文件
    std::vector<std::string> ZFile::readDir(const std::string& dirPath)// 读取目录，返回所有条目名（对应原来的 readDir）
    {
        std::vector<std::string> result;
        namespace fs = std::filesystem;

        std::error_code ec;
        if (!fs::exists(dirPath, ec) || !fs::is_directory(dirPath, ec))
        {
            logging::log(LogL::Error,
                "[zFile]readDir:目录不存在或不是目录 " + dirPath + "  " + ec.message());
            return result;
        }

        for (const auto& entry : fs::directory_iterator(dirPath, ec))
        {
            std::string name = entry.path().filename().string();

            // 子目录加个斜杠区分
            if (entry.is_directory(ec))
                name += "/";

            result.push_back(name);
        }

        if (ec)
        {
            logging::log(LogL::Warning,
                "[zFile]readDir:遍历出错 " + ec.message());
        }

        return result;
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

    bool ZFile::writeFileMap(std::string key, std::string value)//键值追加/修改
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

    std::string ZFile::mapGet(const std::string& key, const std::string& def)
    {
        auto m = readFileMap();
        auto it = m.find(key);
        return (it != m.end()) ? it->second : def;
    }

    std::string ZFile::txtGet(int linen)
    {
        if (!zF.is_open())
        {
            logging::log(LogL::Error, "[zFile]readLineN:文件未打开");
            return "";
        }
        if (linen < 1) return "";

        // 关键：读之前把读指针挪回开头，并清掉 EOF
        zF.clear();
        zF.seekg(0, std::ios::beg);

        std::string line;
        int cur = 0;
        while (std::getline(zF, line))
        {
            // 去行尾 \r
            if (!line.empty() && line.back() == '\r')
                line.pop_back();

            if (++cur == linen) return line;
        }
        return "";
    }

    ZFile::~ZFile() 
    {
        closeFile();
    }
}