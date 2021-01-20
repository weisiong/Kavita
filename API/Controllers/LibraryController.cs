﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using API.DTOs;
using API.Entities;
using API.Extensions;
using API.Interfaces;
using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace API.Controllers
{
    [Authorize]
    public class LibraryController : BaseApiController
    {
        private readonly IDirectoryService _directoryService;
        private readonly ILogger<LibraryController> _logger;
        private readonly IMapper _mapper;
        private readonly ITaskScheduler _taskScheduler;
        private readonly IUnitOfWork _unitOfWork;

        public LibraryController(IDirectoryService directoryService, 
            ILogger<LibraryController> logger, IMapper mapper, ITaskScheduler taskScheduler, 
            IUnitOfWork unitOfWork)
        {
            _directoryService = directoryService;
            _logger = logger;
            _mapper = mapper;
            _taskScheduler = taskScheduler;
            _unitOfWork = unitOfWork;
        }
        
        /// <summary>
        /// Creates a new Library. Upon library creation, adds new library to all Admin accounts.
        /// </summary>
        /// <param name="createLibraryDto"></param>
        /// <returns></returns>
        [Authorize(Policy = "RequireAdminRole")]
        [HttpPost("create")]
        public async Task<ActionResult> AddLibrary(CreateLibraryDto createLibraryDto)
        {
            if (await _unitOfWork.LibraryRepository.LibraryExists(createLibraryDto.Name))
            {
                return BadRequest("Library name already exists. Please choose a unique name to the server.");
            }
            
            var library = new Library
            {
                Name = createLibraryDto.Name,
                Type = createLibraryDto.Type,
                Folders = createLibraryDto.Folders.Select(x => new FolderPath {Path = x}).ToList()
            };

            _unitOfWork.LibraryRepository.Add(library);
            
            var admins = (await _unitOfWork.UserRepository.GetAdminUsersAsync()).ToList();
            foreach (var admin in admins)
            {
                admin.Libraries ??= new List<Library>();
                admin.Libraries.Add(library);
            }
            

            if (!await _unitOfWork.Complete()) return BadRequest("There was a critical issue. Please try again.");

            _logger.LogInformation($"Created a new library: {library.Name}");
            _taskScheduler.ScanLibrary(library.Id);
            return Ok();
        }

        /// <summary>
        /// Returns a list of directories for a given path. If path is empty, returns root drives.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        [Authorize(Policy = "RequireAdminRole")]
        [HttpGet("list")]
        public ActionResult<IEnumerable<string>> GetDirectories(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return Ok(Directory.GetLogicalDrives());
            }

            if (!Directory.Exists(path)) return BadRequest("This is not a valid path");

            return Ok(_directoryService.ListDirectory(path));
        }
        
        [HttpGet]
        public async Task<ActionResult<IEnumerable<LibraryDto>>> GetLibraries()
        {
            return Ok(await _unitOfWork.LibraryRepository.GetLibraryDtosAsync());
        }

        [Authorize(Policy = "RequireAdminRole")]
        [HttpPost("grant-access")]
        public async Task<ActionResult<MemberDto>> UpdateUserLibraries(UpdateLibraryForUserDto updateLibraryForUserDto)
        {
            var user = await _unitOfWork.UserRepository.GetUserByUsernameAsync(updateLibraryForUserDto.Username);
            if (user == null) return BadRequest("Could not validate user");
            
            var libraryString = String.Join(",", updateLibraryForUserDto.SelectedLibraries.Select(x => x.Name));
            _logger.LogInformation($"Granting user {updateLibraryForUserDto.Username} access to: {libraryString}");
            
            var allLibraries = await _unitOfWork.LibraryRepository.GetLibrariesAsync();
            foreach (var library in allLibraries)
            {
                library.AppUsers ??= new List<AppUser>();
                var libraryContainsUser = library.AppUsers.Any(u => u.UserName == user.UserName);
                var libraryIsSelected = updateLibraryForUserDto.SelectedLibraries.Any(l => l.Id == library.Id);
                if (libraryContainsUser && !libraryIsSelected)
                {
                    // Remove 
                    library.AppUsers.Remove(user);
                }
                else if (!libraryContainsUser && libraryIsSelected)
                {
                    library.AppUsers.Add(user);
                } 
                
            }
            
            if (!_unitOfWork.HasChanges())
            {
                _logger.LogInformation($"Added: {updateLibraryForUserDto.SelectedLibraries} to {updateLibraryForUserDto.Username}");
                return Ok(_mapper.Map<MemberDto>(user));
            }

            if (await _unitOfWork.Complete())
            {
                _logger.LogInformation($"Added: {updateLibraryForUserDto.SelectedLibraries} to {updateLibraryForUserDto.Username}");
                return Ok(_mapper.Map<MemberDto>(user));
            }
            
            
            return BadRequest("There was a critical issue. Please try again.");
        }

        [Authorize(Policy = "RequireAdminRole")]
        [HttpPost("scan")]
        public ActionResult Scan(int libraryId)
        {
            _taskScheduler.ScanLibrary(libraryId, true);
            return Ok();
        }

        [HttpGet("libraries")]
        public async Task<ActionResult<IEnumerable<LibraryDto>>> GetLibrariesForUser()
        {
            return Ok(await _unitOfWork.LibraryRepository.GetLibraryDtosForUsernameAsync(User.GetUsername()));
        }

        [HttpGet("series")]
        public async Task<ActionResult<IEnumerable<Series>>> GetSeriesForLibrary(int libraryId)
        {
            var user = await _unitOfWork.UserRepository.GetUserByUsernameAsync(User.GetUsername());
            return Ok(await _unitOfWork.SeriesRepository.GetSeriesDtoForLibraryIdAsync(libraryId, user.Id));
        }

        [Authorize(Policy = "RequireAdminRole")]
        [HttpDelete("delete")]
        public async Task<ActionResult<bool>> DeleteLibrary(int libraryId)
        {
            var username = User.GetUsername();
            _logger.LogInformation($"Library {libraryId} is being deleted by {username}.");
            var series = await _unitOfWork.SeriesRepository.GetSeriesForLibraryIdAsync(libraryId);
            var volumes = (await _unitOfWork.SeriesRepository.GetVolumesForSeriesAsync(series.Select(x => x.Id).ToArray()))
                                .Select(x => x.Id).ToArray();
            var result = await _unitOfWork.LibraryRepository.DeleteLibrary(libraryId);
            
            if (result && volumes.Any())
            {
                _taskScheduler.CleanupVolumes(volumes);
            }
            
            return Ok(result);
        }

        [Authorize(Policy = "RequireAdminRole")]
        [HttpPost("update")]
        public async Task<ActionResult> UpdateLibrary(UpdateLibraryDto libraryForUserDto)
        {
            var library = await _unitOfWork.LibraryRepository.GetLibraryForIdAsync(libraryForUserDto.Id);

            var originalFolders = library.Folders.Select(x => x.Path);
            var differenceBetweenFolders = originalFolders.Except(libraryForUserDto.Folders);

            library.Name = libraryForUserDto.Name;
            library.Folders = libraryForUserDto.Folders.Select(s => new FolderPath() {Path = s}).ToList();

            _unitOfWork.LibraryRepository.Update(library);

            if (!await _unitOfWork.Complete()) return BadRequest("There was a critical issue updating the library.");
            if (differenceBetweenFolders.Any())
            {
                _taskScheduler.ScanLibrary(library.Id, true);
            }
                
            return Ok();

        }
    }
}